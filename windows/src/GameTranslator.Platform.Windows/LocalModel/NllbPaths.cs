namespace GameTranslator.Platform.Windows.LocalModel;

/// <summary>Cấu trúc thư mục dữ liệu NLLB. <paramref name="Root"/> là `...\GameTranslator\nllb`,
/// <paramref name="BundleDir"/> là thư mục `nllb\` cạnh exe (uv.exe, nllb_host.py, requirements-*.txt).</summary>
public sealed record NllbPaths(string Root, string BundleDir, string ModelsDir, IReadOnlyDictionary<string, string>? ModelOverrides = null)
{
    public string UvExe => Path.Combine(BundleDir, "uv.exe");
    public string PythonDir => Path.Combine(Root, "python");
    public string Venv => Path.Combine(Root, "venv");
    public string VenvPython => Path.Combine(Venv, "Scripts", "python.exe");
    public string HostDir => Path.Combine(Root, "host");
    public string HostScript => Path.Combine(HostDir, "nllb_host.py");
    public string EnvJson => Path.Combine(Root, "env.json");
    public string ModelDir(string repoId) =>
        ModelOverrides is not null && ModelOverrides.TryGetValue(repoId, out var p) && Directory.Exists(p) ? p : Path.Combine(ModelsDir, repoId.Replace("/", "--"));
    public string Requirements(string backend) => Path.Combine(HostDir, $"requirements-{backend}.txt");
}
