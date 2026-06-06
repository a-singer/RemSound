import Foundation
import Combine

class LogService: ObservableObject {
    static let shared = LogService()
    
    @Published var logs: [String] = []
    private let maxLogs = 100
    
    func log(_ message: String) {
        let timestamp = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        let formattedMessage = "[\(timestamp)] \(message)"
        
        DispatchQueue.main.async {
            self.logs.insert(formattedMessage, at: 0)
            if self.logs.count > self.maxLogs {
                self.logs.removeLast()
            }
            print(formattedMessage) // Also print to system console
        }
    }
    
    func clear() {
        DispatchQueue.main.async {
            self.logs.removeAll()
        }
    }
}
