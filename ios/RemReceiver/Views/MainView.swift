import SwiftUI

struct MainView: View {
    @StateObject private var viewModel = RemReceiverViewModel()
    
    var body: some View {
        NavigationView {
            Form {
                Section(header: Text("Connection").accessibilityAddTraits(.isHeader)) {
                    TextField("Sender IP or Hostname", text: $viewModel.targetSender)
                        .autocapitalization(.none)
                        .disableAutocorrection(true)
                        .accessibilityLabel("Sender address")
                        .accessibilityHint("Enter the IP address or Tailscale name of your Windows PC")
                    
                    SecureField("Stream Password", text: $viewModel.password)
                        .accessibilityLabel("Stream password")
                        .accessibilityHint("Enter the same password as configured on your Windows sender")
                    
                    Button(action: {
                        viewModel.toggleReceiver()
                    }) {
                        HStack {
                            Text(viewModel.isRunning ? "Stop Receiver" : "Start Receiver")
                            Spacer()
                            Image(systemName: viewModel.isRunning ? "stop.fill" : "play.fill")
                        }
                    }
                    .accessibilityLabel(viewModel.isRunning ? "Stop listening for audio" : "Start listening for audio")
                }
                
                Section(header: Text("Audio Settings").accessibilityAddTraits(.isHeader)) {
                    VStack {
                        HStack {
                            Text("Volume: \(Int(viewModel.volume))%")
                            Spacer()
                            Button(action: { viewModel.toggleMute() }) {
                                Image(systemName: viewModel.isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                            }
                            .accessibilityLabel(viewModel.isMuted ? "Unmute" : "Mute")
                        }
                        Slider(value: $viewModel.volume, in: 0...100, step: 1)
                            .accessibilityLabel("Volume slider")
                            .accessibilityValue("\(Int(viewModel.volume)) percent")
                    }
                    
                    VStack {
                        Text("Buffer: \(Int(viewModel.bufferMs))ms")
                        Slider(value: $viewModel.bufferMs, in: 10...500, step: 10, onEditingChanged: { _ in
                            viewModel.updateBufferDuration()
                        })
                        .accessibilityLabel("Buffer duration slider")
                        .accessibilityValue("\(Int(viewModel.bufferMs)) milliseconds")
                        .accessibilityHint("Lower values reduce latency but may cause stuttering on poor networks")
                    }
                }
                
                Section(header: Text("Remote Control").accessibilityAddTraits(.isHeader)) {
                    HStack {
                        Button("PC Vol Up") { viewModel.sendVolumeUp() }
                            .buttonStyle(.bordered)
                        Spacer()
                        Button("PC Vol Down") { viewModel.sendVolumeDown() }
                            .buttonStyle(.bordered)
                        Spacer()
                        Button("PC Mute") { viewModel.toggleMute() }
                            .buttonStyle(.bordered)
                    }
                    .accessibilityElement(children: .contain)
                    .accessibilityLabel("Remote volume controls for Windows PC")
                }
                
                Section(header: Text("Status").accessibilityAddTraits(.isHeader)) {
                    HStack {
                        Circle()
                            .fill(viewModel.isRunning ? Color.green : Color.red)
                            .frame(width: 10, height: 10)
                        Text(viewModel.status)
                            .accessibilityLabel("Current status: \(viewModel.status)")
                    }
                }
            }
            .navigationTitle("RemSound Receiver")
        }
    }
}

struct MainView_Previews: PreviewProvider {
    static var previews: some View {
        MainView()
    }
}
