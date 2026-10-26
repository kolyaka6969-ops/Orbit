import AudioToolbox
import AVFoundation

/// Аудио и видео через AVFoundation и AudioToolbox. Кодировщика MP3 в macOS нет, поэтому MP3 только читается.
enum MediaBackend {
    static let audioTargets: [(ext: String, subtitle: String?)] = [
        ("m4a", "AAC"),
        ("wav", "без сжатия"),
        ("aiff", "без сжатия"),
        ("flac", "без потерь"),
    ]

    static let videoTargets: [(ext: String, subtitle: String?)] = [
        ("mp4", "H.264"),
        ("mov", "QuickTime"),
    ]

    static func preset(for quality: VideoQuality) -> String {
        switch quality {
        case .small: return AVAssetExportPreset1280x720
        case .balanced: return AVAssetExportPreset1920x1080
        case .high: return AVAssetExportPresetHighestQuality
        }
    }

    // MARK: звук

    /// Декодирует звук любого файла, который читает AVFoundation (в том числе видео), и кодирует его в `ext`.
    static func toAudio(_ src: URL, _ dst: URL, ext: String, bitrate: Int, progress: @escaping (Double) -> Void) async throws {
        let asset = AVURLAsset(url: src)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else {
            throw OrbitError("В файле нет звуковой дорожки.")
        }
        let duration = try await asset.load(.duration).seconds
        var sampleRate = 44_100.0
        var channels: UInt32 = 2
        if let description = try await track.load(.formatDescriptions).first,
           let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(description)?.pointee {
            if asbd.mSampleRate > 0 { sampleRate = asbd.mSampleRate }
            channels = max(1, min(2, asbd.mChannelsPerFrame))
        }
        if ext == "m4a" { sampleRate = min(sampleRate, 48_000) }

        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderAudioMixOutput(audioTracks: [track], audioSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
            AVLinearPCMIsBigEndianKey: false,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: channels,
        ])
        reader.add(output)

        var client = AudioStreamBasicDescription(
            mSampleRate: sampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4 * channels, mFramesPerPacket: 1, mBytesPerFrame: 4 * channels,
            mChannelsPerFrame: channels, mBitsPerChannel: 32, mReserved: 0)
        var (fileType, format) = destinationFormat(ext, sampleRate, channels)

        var fileRef: ExtAudioFileRef?
        var status = ExtAudioFileCreateWithURL(dst as CFURL, fileType, &format, nil, AudioFileFlags.eraseFile.rawValue, &fileRef)
        guard status == noErr, let file = fileRef else {
            throw OrbitError("Не удалось создать файл \(ext.uppercased()) (код \(status)).")
        }
        var disposed = false
        defer { if !disposed { ExtAudioFileDispose(file) } }

        status = ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat,
                                         UInt32(MemoryLayout<AudioStreamBasicDescription>.size), &client)
        guard status == noErr else { throw OrbitError("Кодировщик \(ext.uppercased()) недоступен (код \(status)).") }
        if ext == "m4a" { setBitrate(file, bitrate) }

        guard reader.startReading() else { throw reader.error ?? OrbitError("Не удалось прочитать звук.") }
        while let buffer = output.copyNextSampleBuffer() {
            if Task.isCancelled {
                reader.cancelReading()
                throw CancellationError()
            }
            let frames = CMSampleBufferGetNumSamples(buffer)
            guard frames > 0, let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
            let length = CMBlockBufferGetDataLength(block)
            var data = Data(count: length)
            let copied = data.withUnsafeMutableBytes { raw in
                CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: raw.baseAddress!)
            }
            guard copied == kCMBlockBufferNoErr else { throw OrbitError("Не удалось прочитать звук.") }
            status = data.withUnsafeMutableBytes { raw -> OSStatus in
                var list = AudioBufferList(
                    mNumberBuffers: 1,
                    mBuffers: AudioBuffer(mNumberChannels: channels, mDataByteSize: UInt32(length), mData: raw.baseAddress))
                return ExtAudioFileWrite(file, UInt32(frames), &list)
            }
            guard status == noErr else {
                reader.cancelReading()
                throw OrbitError("Ошибка кодирования \(ext.uppercased()) (код \(status)).")
            }
            if duration > 0 {
                progress(CMSampleBufferGetPresentationTimeStamp(buffer).seconds / duration)
            }
        }
        if reader.status == .failed { throw reader.error ?? OrbitError("Не удалось прочитать звук.") }
        disposed = true
        status = ExtAudioFileDispose(file)
        guard status == noErr else { throw OrbitError("Не удалось завершить запись \(ext.uppercased()) (код \(status)).") }
    }

    private static func destinationFormat(_ ext: String, _ rate: Double, _ channels: UInt32) -> (AudioFileTypeID, AudioStreamBasicDescription) {
        var f = AudioStreamBasicDescription()
        f.mSampleRate = rate
        f.mChannelsPerFrame = channels
        switch ext {
        case "wav", "aiff":
            f.mFormatID = kAudioFormatLinearPCM
            f.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked | (ext == "aiff" ? kAudioFormatFlagIsBigEndian : 0)
            f.mBitsPerChannel = 16
            f.mFramesPerPacket = 1
            f.mBytesPerFrame = 2 * channels
            f.mBytesPerPacket = 2 * channels
            return (ext == "wav" ? kAudioFileWAVEType : kAudioFileAIFFType, f)
        case "flac":
            f.mFormatID = kAudioFormatFLAC
            f.mFormatFlags = 1 // kAppleLosslessFormatFlag_16BitSourceData: FLAC берёт флаги Apple Lossless.
            completeFormat(&f)
            return (kAudioFileFLACType, f)
        default:
            f.mFormatID = kAudioFormatMPEG4AAC
            completeFormat(&f)
            return (kAudioFileM4AType, f)
        }
    }

    /// Дополняет описание сжатого формата (размер пакета и т. п.) значениями самого кодировщика.
    private static func completeFormat(_ f: inout AudioStreamBasicDescription) {
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        AudioFormatGetProperty(kAudioFormatProperty_FormatInfo, 0, nil, &size, &f)
    }

    private static func setBitrate(_ file: ExtAudioFileRef, _ bitrate: Int) {
        var converterRef: AudioConverterRef?
        var size = UInt32(MemoryLayout<AudioConverterRef?>.size)
        guard ExtAudioFileGetProperty(file, kExtAudioFileProperty_AudioConverter, &size, &converterRef) == noErr,
              let converter = converterRef else { return }
        var rate = UInt32(bitrate)
        AudioConverterSetProperty(converter, kAudioConverterEncodeBitRate, UInt32(MemoryLayout<UInt32>.size), &rate)
        // Пустая конфигурация заставляет ExtAudioFile пересоздать кодировщик с новым битрейтом.
        var config: CFArray?
        ExtAudioFileSetProperty(file, kExtAudioFileProperty_ConverterConfig, UInt32(MemoryLayout<CFArray?>.size), &config)
    }

    // MARK: видео

    static func exportVideo(_ src: URL, _ dst: URL, ext: String, preset: String, mute: Bool = false,
                            progress: @escaping (Double) -> Void) async throws {
        let asset = AVURLAsset(url: src)
        var source: AVAsset = asset
        if mute {
            let composition = AVMutableComposition()
            let duration = try await asset.load(.duration)
            for track in try await asset.loadTracks(withMediaType: .video) {
                guard let copy = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
                try copy.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: track, at: .zero)
                copy.preferredTransform = try await track.load(.preferredTransform)
            }
            source = composition
        }
        guard let session = AVAssetExportSession(asset: source, presetName: preset) else {
            throw OrbitError("Это видео нельзя перекодировать.")
        }
        session.outputURL = dst
        session.outputFileType = ext == "mov" ? .mov : .mp4
        session.shouldOptimizeForNetworkUse = true

        let poll = Task {
            while !Task.isCancelled {
                progress(Double(session.progress))
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        defer { poll.cancel() }

        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                session.exportAsynchronously {
                    switch session.status {
                    case .completed: cont.resume()
                    case .cancelled: cont.resume(throwing: CancellationError())
                    default: cont.resume(throwing: session.error ?? OrbitError("Не удалось перекодировать видео."))
                    }
                }
            }
        } onCancel: {
            session.cancelExport()
        }
    }
}
