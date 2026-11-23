import AVFoundation
import CoreImage
import CoreMedia
import Foundation

enum CaptureStatus: Equatable {
    case idle
    case running(width: Int, height: Int, fps: Int, codec: String)
    case interrupted
    case disconnected(String)
    case failed(String)
}

/// Một `AVCaptureSession` duy nhất: hình (preview layer + lấy mẫu khung) và tiếng (audio preview output).
final class CaptureController: NSObject, @unchecked Sendable {
    /// Session hình. Tiếng chạy ở session riêng để hai luồng không phụ thuộc nhau (giảm trễ hình).
    let session = AVCaptureSession()
    private let audioSession = AVCaptureSession()

    /// Gọi trên queue lấy khung với MỌI khung (dùng cho bộ vẽ Metal); nil thì không tốn gì.
    var displayHandler: (@Sendable (CVPixelBuffer) -> Void)? {
        get { lock.lock(); defer { lock.unlock() }; return _displayHandler }
        set { lock.lock(); _displayHandler = newValue; lock.unlock() }
    }
    private var _displayHandler: (@Sendable (CVPixelBuffer) -> Void)?

    /// Gọi trên queue lấy khung, chỉ với 1 trên mỗi N khung.
    var frameHandler: (@Sendable (CVPixelBuffer) -> Void)?
    var statusHandler: (@Sendable (CaptureStatus) -> Void)?

    private let sessionQueue = DispatchQueue(label: "vn.gametranslator.capture.session")
    private let frameQueue = DispatchQueue(label: "vn.gametranslator.capture.frames", qos: .userInitiated)
    private let videoOutput = AVCaptureVideoDataOutput()
    private let audioPreview = AVCaptureAudioPreviewOutput()

    private let lock = NSLock()
    private var sampleEvery = 15
    private var frameCounter = 0
    private var requestedVideoID: String?
    private var requestedAudioID: String?
    private var observers: [NSObjectProtocol] = []
    private var latestBuffer: CVPixelBuffer?
    private var videoDims: (width: Int, height: Int) = (0, 0)
    private var videoCodec = ""
    private var fpsWindowStart: CFTimeInterval = 0
    private var fpsFrames = 0
    private let snapshotContext = CIContext()

    override init() {
        super.init()
        videoOutput.alwaysDiscardsLateVideoFrames = true
        // Không ép định dạng điểm ảnh: nhận nguyên định dạng của thiết bị, đỡ một bước chuyển đổi mỗi khung.
        videoOutput.setSampleBufferDelegate(self, queue: frameQueue)
        observeNotifications()
    }

    deinit { observers.forEach(NotificationCenter.default.removeObserver) }

    func setSampleEvery(_ n: Int) {
        lock.lock(); sampleEvery = max(n, 1); lock.unlock()
    }

    /// Ảnh tĩnh của khung mới nhất (trễ tối đa một chu kỳ lấy mẫu), dùng cho màn hình chọn vùng.
    func snapshot() -> CGImage? {
        lock.lock(); let buffer = latestBuffer; lock.unlock()
        guard let buffer else { return nil }
        let image = CIImage(cvPixelBuffer: buffer)
        return snapshotContext.createCGImage(image, from: image.extent)
    }

    func setVolume(_ volume: Float) {
        sessionQueue.async { self.audioPreview.volume = volume }
    }

    func setAudioOutput(uniqueID: String?) {
        sessionQueue.async { self.audioPreview.outputDeviceUniqueID = uniqueID }
    }

    /// Mở lại session với thiết bị chọn; `audioID` nil nghĩa là không phát tiếng.
    func start(videoID: String, audioID: String?, volume: Float, outputUID: String?) {
        lock.lock()
        requestedVideoID = videoID
        requestedAudioID = audioID
        lock.unlock()
        sessionQueue.async { self.configureAndRun(volume: volume, outputUID: outputUID) }
    }

    func stop() {
        lock.lock(); requestedVideoID = nil; requestedAudioID = nil; lock.unlock()
        sessionQueue.async {
            if self.session.isRunning { self.session.stopRunning() }
            if self.audioSession.isRunning { self.audioSession.stopRunning() }
            self.statusHandler?(.idle)
        }
    }

    // MARK: - Cấu hình (chạy trên sessionQueue)

    private func configureAndRun(volume: Float, outputUID: String?) {
        lock.lock()
        let videoID = requestedVideoID
        let audioID = requestedAudioID
        lock.unlock()

        guard let videoID, let device = AVCaptureDevice(uniqueID: videoID) else {
            statusHandler?(.disconnected("Không thấy thiết bị hình"))
            return
        }
        if session.isRunning { session.stopRunning() }

        session.beginConfiguration()
        session.inputs.forEach(session.removeInput)
        session.outputs.forEach(session.removeOutput)

        do {
            let videoInput = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(videoInput) else { throw CaptureError.cannotAdd("input hình") }
            session.addInput(videoInput)

            if session.canAddOutput(videoOutput) { session.addOutput(videoOutput) }
        } catch {
            session.commitConfiguration()
            statusHandler?(.failed(error.localizedDescription))
            return
        }
        session.commitConfiguration()

        _ = FormatSelector.apply(to: device)
        session.startRunning()
        // macOS có thể áp lại format mặc định lúc session chạy: đặt lại một lần nữa để chắc chắn giữ 1080p60.
        _ = FormatSelector.apply(to: device)
        configureAudio(audioID: audioID, volume: volume, outputUID: outputUID)
        let dims = CMVideoFormatDescriptionGetDimensions(device.activeFormat.formatDescription)
        let codec = FormatSelector.codecName(device.activeFormat)
        // FPS đọc lại sau khi chạy: đây là giá trị thiết bị thật sự đang dùng.
        let active = device.activeVideoMinFrameDuration
        let fps = active.isNumeric && active.seconds > 0 ? Int((1 / active.seconds).rounded()) : 0
        lock.lock()
        videoDims = (Int(dims.width), Int(dims.height))
        videoCodec = codec
        fpsWindowStart = 0
        fpsFrames = 0
        lock.unlock()
        statusHandler?(.running(width: Int(dims.width), height: Int(dims.height), fps: fps, codec: codec))
        Log.capture.info("Capture bắt đầu: \(device.localizedName, privacy: .public) \(dims.width)x\(dims.height)@\(fps)")
    }

    /// Tiếng chạy ở session riêng, không chung với hình.
    private func configureAudio(audioID: String?, volume: Float, outputUID: String?) {
        guard let audioID, let audioDevice = AVCaptureDevice(uniqueID: audioID) else {
            if audioSession.isRunning { audioSession.stopRunning() }
            return
        }
        if audioSession.isRunning { audioSession.stopRunning() }
        audioSession.beginConfiguration()
        audioSession.inputs.forEach(audioSession.removeInput)
        audioSession.outputs.forEach(audioSession.removeOutput)
        if let input = try? AVCaptureDeviceInput(device: audioDevice), audioSession.canAddInput(input) {
            audioSession.addInput(input)
            if audioSession.canAddOutput(audioPreview) {
                audioSession.addOutput(audioPreview)
                audioPreview.volume = volume
                audioPreview.outputDeviceUniqueID = outputUID
            }
        }
        audioSession.commitConfiguration()
        audioSession.startRunning()
    }

    private enum CaptureError: LocalizedError {
        case cannotAdd(String)
        var errorDescription: String? {
            switch self { case .cannotAdd(let what): return "Không thêm được \(what) vào session" }
        }
    }

    // MARK: - Rút/cắm lại và gián đoạn

    private func observeNotifications() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVCaptureDevice.wasDisconnectedNotification, object: nil, queue: nil) { [weak self] note in
            guard let self, let device = note.object as? AVCaptureDevice, device.uniqueID == self.currentVideoID else { return }
            self.statusHandler?(.disconnected(device.localizedName))
        })
        observers.append(center.addObserver(forName: AVCaptureDevice.wasConnectedNotification, object: nil, queue: nil) { [weak self] note in
            guard let self, let device = note.object as? AVCaptureDevice, device.uniqueID == self.currentVideoID else { return }
            self.sessionQueue.async { self.configureAndRun(volume: self.audioPreview.volume, outputUID: self.audioPreview.outputDeviceUniqueID) }
        })
        observers.append(center.addObserver(forName: AVCaptureSession.wasInterruptedNotification, object: session, queue: nil) { [weak self] _ in
            self?.statusHandler?(.interrupted)
        })
        observers.append(center.addObserver(forName: AVCaptureSession.runtimeErrorNotification, object: session, queue: nil) { [weak self] note in
            let error = note.userInfo?[AVCaptureSessionErrorKey] as? Error
            self?.statusHandler?(.failed(error?.localizedDescription ?? "Lỗi session"))
        })
    }

    private var currentVideoID: String? {
        lock.lock(); defer { lock.unlock() }
        return requestedVideoID
    }
}

extension CaptureController: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        lock.lock()
        frameCounter += 1
        let due = frameCounter % sampleEvery == 0
        // Đo FPS thực tế nhận được, báo lên thanh trạng thái mỗi ~2 giây.
        let now = CACurrentMediaTime()
        if fpsWindowStart == 0 { fpsWindowStart = now }
        fpsFrames += 1
        var report: (Int, Int, Int, String)?
        let display = _displayHandler
        if now - fpsWindowStart >= 2 {
            report = (videoDims.width, videoDims.height, Int((Double(fpsFrames) / (now - fpsWindowStart)).rounded()), videoCodec)
            fpsWindowStart = now
            fpsFrames = 0
        }
        lock.unlock()
        if let display, let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) { display(pixels) }
        if let report, report.0 > 0 {
            statusHandler?(.running(width: report.0, height: report.1, fps: report.2, codec: report.3))
            Log.capture.info("FPS thực nhận: \(report.2)")
        }
        guard due, let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lock.lock(); latestBuffer = buffer; lock.unlock()
        frameHandler?(buffer)
    }
}

/// Chọn định dạng: ưu tiên 1920x1080 đạt 60 fps.
enum FormatSelector {
    static func isCompressed(_ format: AVCaptureDevice.Format) -> Bool {
        let sub = CMFormatDescriptionGetMediaSubType(format.formatDescription)
        return sub == kCMVideoCodecType_JPEG || sub == kCMVideoCodecType_JPEG_OpenDML
            || sub == kCMVideoCodecType_H264 || sub == kCMVideoCodecType_HEVC
    }

    /// Tên định dạng để hiện ở thẻ góc video (thô hay nén).
    static func codecName(_ format: AVCaptureDevice.Format) -> String {
        let sub = CMFormatDescriptionGetMediaSubType(format.formatDescription)
        switch sub {
        case kCMVideoCodecType_JPEG, kCMVideoCodecType_JPEG_OpenDML: return "MJPEG"
        case kCMVideoCodecType_H264: return "H.264"
        case kCMVideoCodecType_HEVC: return "HEVC"
        default: return "thô"
        }
    }

    /// Trả về FPS thực tế đã đặt.
    static func apply(to device: AVCaptureDevice) -> Int {
        func dims(_ f: AVCaptureDevice.Format) -> CMVideoDimensions {
            CMVideoFormatDescriptionGetDimensions(f.formatDescription)
        }
        func maxFPS(_ f: AVCaptureDevice.Format) -> Double {
            f.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 0
        }
        let formats = device.formats
        let full = formats.filter { dims($0).width == 1920 && dims($0).height == 1080 }
        // Ưu tiên định dạng thô (không nén) ở 1080p60: MJPEG/H.264 phải giải mã nên trễ hơn.
        let full60 = full.filter { maxFPS($0) >= 60 }
        let best = full60.first(where: { !isCompressed($0) })
            ?? full60.first
            ?? full.max(by: { maxFPS($0) < maxFPS($1) })
            ?? formats.max(by: { Int(dims($0).width) * Int(dims($0).height) < Int(dims($1).width) * Int(dims($1).height) })

        guard let best else { return 0 }
        do {
            try device.lockForConfiguration()
            defer { device.unlockForConfiguration() }
            device.activeFormat = best
            // Dùng đúng range của chính format đó: tự đặt CMTime (vd 1/60) mà không nằm
            // trong videoSupportedFrameRateRanges sẽ ném NSException -> abort() (capture card UVC hay gặp).
            let ranges = best.videoSupportedFrameRateRanges
            let target = ranges.first(where: { $0.maxFrameRate >= 60 && $0.minFrameRate <= 60 })
                ?? ranges.max(by: { $0.maxFrameRate < $1.maxFrameRate })
            if let target, target.minFrameDuration.isValid, target.minFrameDuration.isNumeric {
                // minFrameDuration = FPS cao nhất của range; nếu range cho phép 60 thì chọn 60.
                let wants60 = target.maxFrameRate > 60 && target.minFrameRate <= 60
                let duration = wants60 ? CMTime(value: 1, timescale: 60) : target.minFrameDuration
                let supported = ranges.contains {
                    CMTimeCompare(duration, $0.minFrameDuration) >= 0 && CMTimeCompare(duration, $0.maxFrameDuration) <= 0
                }
                if supported, device.activeFormat.videoSupportedFrameRateRanges.contains(where: {
                    CMTimeCompare(duration, $0.minFrameDuration) >= 0 && CMTimeCompare(duration, $0.maxFrameDuration) <= 0
                }) {
                    device.activeVideoMinFrameDuration = duration
                    device.activeVideoMaxFrameDuration = duration
                } else {
                    Log.capture.error("Bỏ qua đặt frame duration: không nằm trong range hỗ trợ")
                }
            }
            let active = device.activeVideoMinFrameDuration
            if active.isNumeric, active.seconds > 0 { return Int((1 / active.seconds).rounded()) }
            return Int(min(60, maxFPS(best)).rounded())
        } catch {
            Log.capture.error("Không đặt được định dạng: \(error.localizedDescription, privacy: .public)")
            return Int(maxFPS(device.activeFormat).rounded())
        }
    }
}
