import Foundation
import Vision

/// Распознавание текста встроенным в macOS движком Vision (русский и английский).
enum OcrBackend {
    static func recognize(_ url: URL) throws -> String {
        let image = try ImageBackend.load(url).image
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        let supported = (try? request.supportedRecognitionLanguages()) ?? []
        let languages = ["ru-RU", "en-US"].filter { supported.contains($0) }
        if !languages.isEmpty { request.recognitionLanguages = languages }
        try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        let lines = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
        return lines.joined(separator: "\n")
    }
}
