import Foundation
import Combine

class LogService: ObservableObject {
    static let shared = LogService()

    @Published var logs: [String] = []
    private let maxMemLogs = 20
    private let maxFileLogs = 500
    private let logFileName = "remsound.log"
    private let fileQueue = DispatchQueue(label: "com.rem.receiver.log", qos: .utility)

    private var logFileURL: URL? {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent(logFileName)
    }

    init() {
        loadLogsFromFile()
        log("Log Service initialized")
    }

    func log(_ message: String) {
        let timestamp = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        let line = "[\(timestamp)] \(message)"
        print(line)
        DispatchQueue.main.async {
            self.logs.insert(line, at: 0)
            if self.logs.count > self.maxMemLogs { self.logs.removeLast() }
        }
        fileQueue.async { [weak self] in self?.writeToPersistentFile(line) }
    }

    private func writeToPersistentFile(_ message: String) {
        guard let url = logFileURL else { return }
        let data = (message + "\n").data(using: .utf8)!
        if FileManager.default.fileExists(atPath: url.path) {
            if let fh = try? FileHandle(forWritingTo: url) {
                fh.seekToEndOfFile()
                fh.write(data)
                fh.closeFile()
                enforceFileLimit()
            }
        } else {
            try? data.write(to: url)
        }
    }

    private func enforceFileLimit() {
        guard let url = logFileURL,
              let content = try? String(contentsOf: url, encoding: .utf8) else { return }
        let lines = content.components(separatedBy: .newlines).filter { !$0.isEmpty }
        if lines.count > maxFileLogs {
            let trimmed = lines.suffix(maxFileLogs - 50).joined(separator: "\n") + "\n"
            try? trimmed.write(to: url, atomically: true, encoding: .utf8)
        }
    }

    private func loadLogsFromFile() {
        guard let url = logFileURL,
              let content = try? String(contentsOf: url, encoding: .utf8) else { return }
        let lines = Array(content.components(separatedBy: .newlines).filter { !$0.isEmpty }.suffix(maxMemLogs).reversed())
        DispatchQueue.main.async { self.logs = lines }
    }

    func clear() {
        if let url = logFileURL { try? FileManager.default.removeItem(at: url) }
        DispatchQueue.main.async { self.logs.removeAll() }
    }

    func getLogFilePath() -> String {
        logFileURL?.path ?? "Unknown"
    }
}
