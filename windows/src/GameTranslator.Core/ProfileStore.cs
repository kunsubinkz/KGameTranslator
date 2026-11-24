using System.Globalization;
using System.Text;

namespace GameTranslator.Core;

/// <summary>Lưu hồ sơ dạng JSON, mỗi hồ sơ một file `&lt;id&gt;.json`.</summary>
public sealed class ProfileStore(string directory)
{
    public string Directory { get; } = directory;

    public static string ApplicationData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameTranslator");

    public static ProfileStore Standard() => new(Path.Combine(ApplicationData, "Profiles"));

    /// <summary>Đảm bảo thư mục tồn tại và có ít nhất các hồ sơ mẫu.</summary>
    public void SeedIfNeeded()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (List().Count == 0)
        {
            Save(Profile.Default);
            Save(Profile.ZeldaTotk);
        }
    }

    public IReadOnlyList<Profile> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var result = new List<Profile>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                if (Profile.FromJson(File.ReadAllText(file)) is { } p) result.Add(p);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public Profile? Load(string id)
    {
        try { return Profile.FromJson(File.ReadAllText(FilePath(id))); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { return null; }
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomic(FilePath(profile.Id), profile.ToJson());
    }

    public void Delete(string id) => File.Delete(FilePath(id));

    public void Export(Profile profile, string path) => WriteAtomic(path, profile.ToJson());

    /// <summary>Nhập hồ sơ từ file JSON; nếu trùng id thì ghi đè.</summary>
    public Profile ImportProfile(string path)
    {
        var profile = Profile.FromJson(File.ReadAllText(path))
            ?? throw new System.Text.Json.JsonException("File hồ sơ rỗng");
        Save(profile);
        return profile;
    }

    /// <summary>Chuyển tên bất kỳ thành id an toàn làm tên file.</summary>
    public static string MakeId(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower == 'đ') lower = 'd';
            sb.Append(char.IsLetterOrDigit(lower) ? lower : '-');
        }
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "profile-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() : slug;
    }

    private string FilePath(string id) => Path.Combine(Directory, id.Replace('/', '_') + ".json");

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
