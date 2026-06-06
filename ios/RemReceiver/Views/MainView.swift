import SwiftUI

struct LogView: View {
    @ObservedObject var logService = LogService.shared
    
    var body: some View {
        List {
            Section(header: Text("Most Recent 20 Entries")) {
                ForEach(logService.logs, id: \.self) { log in
                    Text(log)
                        .font(.system(size: 10, design: .monospaced))
                }
            }
            
            Section {
                Button("Clear All Logs", action: { logService.clear() })
                    .foregroundColor(.red)
            }
        }
        .navigationTitle("Debug Logs")
    }
}

struct MainView: View {
    @StateObject private var viewModel = RemReceiverViewModel()
    @State private var showingLogs = false
    
    var body: some View {
        NavigationView {
            Form {
                Section(header: Text("Connection").accessibilityAddTraits(.isHeader)) {
                    TextField("Sender IP or Hostname", text: $viewModel.targetSender)
                        .autocapitalization(.none)
                        .disableAutocorrection(true)
                        .accessibilityLabel("Sender address")
                    
                    SecureField("Stream Password", text: $viewModel.password)
                        .accessibilityLabel("Stream password")
                    
                    Button(action: { viewModel.toggleReceiver() }) {
                        HStack {
                            Text(viewModel.isRunning ? "Stop Receiver" : "Start Receiver")
                            Spacer()
                            Image(systemName: viewModel.isRunning ? "stop.fill" : "play.fill")
                        }
                    }
                }
                
                Section(header: Text("Audio Settings").accessibilityAddTraits(.isHeader)) {
                    VStack(alignment: .leading) {
                        HStack {
                            Text("Volume: \(Int(viewModel.volume))%")
                            Spacer()
                            Button(action: { viewModel.isLocalMuted.toggle() }) {
                                Image(systemName: viewModel.isLocalMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                            }
                            .accessibilityLabel("Toggle local mute")
                        }
                        Slider(value: $viewModel.volume, in: 0...200, step: 1)
                            .accessibilityValue("\(Int(viewModel.volume)) percent")
                    }
                    
                    VStack(alignment: .leading) {
                        Text("Buffer: \(Int(viewModel.bufferMs))ms")
                        Slider(value: $viewModel.bufferMs, in: 10...500, step: 10)
                            .accessibilityValue("\(Int(viewModel.bufferMs)) milliseconds")
                    }
                }
                
                Section(header: Text("Remote App Control").accessibilityAddTraits(.isHeader)) {
                    HStack {
                        Button("Vol Up") { viewModel.sendVolumeUp() }
                        Spacer()
                        Button("Vol Down") { viewModel.sendVolumeDown() }
                        Spacer()
                        Button("Mute") { viewModel.sendMuteToggle() }
                    }
                    .buttonStyle(.bordered)
                }

                Section(header: Text("Remote System Control").accessibilityAddTraits(.isHeader)) {
                    HStack {
                        Button("Sys Up") { viewModel.sendSystemVolumeUp() }
                        Spacer()
                        Button("Sys Down") { viewModel.sendSystemVolumeDown() }
                        Spacer()
                        Button("Sys Mute") { viewModel.sendSystemMuteToggle() }
                    }
                    .buttonStyle(.bordered)
                }
                
                Section(header: Text("Status").accessibilityAddTraits(.isHeader)) {
                    HStack {
                        Circle().fill(viewModel.isRunning ? Color.green : Color.red).frame(width: 10, height: 10)
                        Text(viewModel.status)
                    }
                }
                
                Section {
                    NavigationLink(destination: LogView(), isActive: $showingLogs) {
                        HStack {
                            Text("View Debug Logs")
                            Spacer()
                            Image(systemName: "list.bullet.rectangle")
                        }
                    }
                    .accessibilityLabel("View debug logs")
                    .accessibilityHint("Shows the most recent 20 events and errors")
                }
            }
            .navigationTitle("RemSound Receiver")
        }
    }
}
