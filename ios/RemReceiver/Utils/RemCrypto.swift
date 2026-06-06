import Foundation
import CommonCrypto
import CryptoKit

class PcmFrameAssembler {
    private var pendingFrameId: UInt32 = 0
    private var pendingTotalParts: Int = 0
    private var assemblyBuffer: Data = Data()
    private var partsReceived: Set<Int> = []
    
    func addPart(frameId: UInt32, partIndex: Int, totalParts: Int, data: Data) -> Data? {
        if frameId != pendingFrameId {
            // New frame, reset
            pendingFrameId = frameId
            pendingTotalParts = totalParts
            assemblyBuffer = Data(repeating: 0, count: 65536) // Sufficient buffer
            partsReceived.removeAll()
        }
        
        guard partIndex < totalParts else { return nil }
        
        let offset = partIndex * 1400 // Assuming standard MTU-friendly split
        if assemblyBuffer.count < offset + data.count {
            assemblyBuffer.count = offset + data.count
        }
        
        assemblyBuffer.replaceSubrange(offset..<(offset + data.count), with: data)
        partsReceived.insert(partIndex)
        
        if partsReceived.count == totalParts {
            let finalData = assemblyBuffer.prefix(offset + data.count)
            partsReceived.removeAll()
            return finalData
        }
        
        return nil
    }
}

enum RemCrypto {
    static let keySalt = "RemSound.v1.audio-key".data(using: .utf8)!
    static let iterations: UInt32 = 100_000
    static let keyLength = 32
    
    static let emptyKey: [UInt8] = [
        231, 254, 148, 233, 109, 124, 250, 108, 81, 186, 142, 21, 144, 245, 14, 55,
        226, 52, 229, 176, 179, 230, 98, 178, 75, 228, 138, 66, 97, 245, 156, 24
    ]
    
    static func deriveKey(password: String) -> Data? {
        if password.isEmpty { return Data(emptyKey) }
        
        var derivedKey = Data(count: keyLength)
        let passwordData = password.data(using: .utf8)!
        
        let status = derivedKey.withUnsafeMutableBytes { derivedBytes in
            passwordData.withUnsafeBytes { passwordBytes in
                keySalt.withUnsafeBytes { saltBytes in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        passwordBytes.baseAddress?.assumingMemoryBound(to: Int8.self),
                        passwordData.count,
                        saltBytes.baseAddress?.assumingMemoryBound(to: UInt8.self),
                        keySalt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                        iterations,
                        derivedBytes.baseAddress?.assumingMemoryBound(to: UInt8.self),
                        keyLength
                    )
                }
            }
        }
        
        return status == kCCSuccess ? derivedKey : nil
    }
    
    static func decrypt(key: Data, data: Data) -> Data? {
        guard data.count >= 28 else { return nil } // 12 nonce + 16 tag
        
        let nonce = data.prefix(12)
        let tag = data.subdata(in: 12..<28)
        let ciphertext = data.suffix(from: 28)
        
        do {
            let aesKey = SymmetricKey(data: key)
            let sealedBox = try AES.GCM.SealedBox(combined: nonce + ciphertext + tag)
            return try AES.GCM.open(sealedBox, using: aesKey)
        } catch {
            return nil
        }
    }
}
