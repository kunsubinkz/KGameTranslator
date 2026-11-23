import AVFoundation
import CoreAudio
import Foundation

struct CaptureDevice: Identifiable, Hashable {
    let id: String
    let name: String
}

enum DeviceDiscovery {
    static func videoDevices() -> [CaptureDevice] {
        let session = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.external, .builtInWideAngleCamera], mediaType: .video, position: .unspecified)
        return session.devices.map { CaptureDevice(id: $0.uniqueID, name: $0.localizedName) }
    }

    /// Capture card thường hiện như một micro; lọc rộng rồi để người dùng chọn, ưu tiên tên giống card.
    static func audioDevices() -> [CaptureDevice] {
        let session = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.microphone, .external], mediaType: .audio, position: .unspecified)
        return session.devices.map { CaptureDevice(id: $0.uniqueID, name: $0.localizedName) }
    }

    static func bestAudioMatch(forVideo video: CaptureDevice?, in audio: [CaptureDevice]) -> CaptureDevice? {
        guard let video else { return audio.first }
        let words = video.name.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
        let scored = audio.map { device -> (CaptureDevice, Int) in
            let name = device.name.lowercased()
            return (device, words.filter { $0.count > 2 && name.contains($0) }.count)
        }
        if let best = scored.max(by: { $0.1 < $1.1 }), best.1 > 0 { return best.0 }
        return nil
    }

    /// Thiết bị phát tiếng (loa, tai nghe) lấy từ CoreAudio; `id` là UID dùng cho `outputDeviceUniqueID`.
    static func audioOutputs() -> [CaptureDevice] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr
        else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr
        else { return [] }

        return ids.compactMap { id in
            guard hasOutputStreams(id), let uid = stringProperty(id, kAudioDevicePropertyDeviceUID),
                  let name = stringProperty(id, kAudioObjectPropertyName) else { return nil }
            return CaptureDevice(id: uid, name: name)
        }
    }

    private static func hasOutputStreams(_ id: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func stringProperty(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }
}

enum PermissionState: Equatable {
    case unknown, granted, denied
}

enum Permissions {
    static func requestCapture() async -> (video: PermissionState, audio: PermissionState) {
        let video = await request(.video)
        let audio = await request(.audio)
        return (video, audio)
    }

    private static func request(_ type: AVMediaType) async -> PermissionState {
        switch AVCaptureDevice.authorizationStatus(for: type) {
        case .authorized: return .granted
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: type) ? .granted : .denied
        default: return .denied
        }
    }
}
