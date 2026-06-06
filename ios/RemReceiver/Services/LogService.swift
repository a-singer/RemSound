import Foundation
import Combine

class LogService: ObservableObject {
    static let shared = LogService()
    
    @Published var logs: [String] = []
    private let maxMemLogs = 20
    private let maxFileLogs = 500
    private let logFileName = "remsound.log"
    
    private var logFileURL: URL? {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?.appendingPathComponent(logFileName)
    }
    
    init() {
        loadLogsFromFile()
        log("Log Service initialized")
    }
    
    func log(_ message: String) {
        let timestamp = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        let formattedMessage = "[\(timestamp)] \(message)"
        
        DispatchQueue.main.async {
            self.logs.insert(formattedMessage, at: 0)
            if self.logs.count > self.maxMemLogs {
                self.logs.removeLast()
            }
            self.writeToPersistentFile(formattedMessage)
            print(formattedMessage)
        }
    }
    
    private func writeToPersistentFile(_ message: String) {
        guard let url = logFileURL else { return }
        
        let data = (message + "\n").data(using: .utf8)!
        if FileManager.default.fileExists(atPath: url.path) {
            if let fileHandle = try? FileHandle(forWritingTo: url) {
                fileHandle.seekToEndOfFile()
                fileHandle.write(data)
                fileHandle.closeFile()
                enforceFileLimit()
            }
        } else {
            try? data.write(to: url)
        }
    }
    
    private func enforceFileLimit() {
        guard let url = logFileURL else { return }
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return }
        
        let lines = content.components(separatedBy: .newlines).filter { !$0.isEmpty }
        if lines.count > maxFileLogs {
            let keptLines = lines.suffix(maxFileLogs - 50) // Trim extra to avoid frequent writes
            let newContent = keptLines.joined(separator: "\n") + "\n"
            try? newContent.write(to: url, atomically: true, encoding: .utf8)
        }
    }
    
    private func loadLogsFromFile() {
        guard let url = logFileURL else { return }
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return }
        
        let lines = content.components(separatedBy: .newlines).filter { !$0.isEmpty }
        let recent = lines.suffix(maxMemLogs).reversed()
        DispatchQueue.main.async {
            self.logs = Array(recent)
        }
    }
    
    func clear() {
        if let url = logFileURL {
            try? FileManager.default.removeItem(at: url)
        }
        DispatchQueue.main.async {
            self.logs.removeAll()
        }
    }
    
    func getLogFilePath() -> String {
        return logFileURL?.path ?? "Unknown"
    }
}
