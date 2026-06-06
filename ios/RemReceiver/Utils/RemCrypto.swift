import Foundation
import CommonCrypto
import CryptoKit

class PcmFrameAssembler {
    private var pendingFrameId: UInt32 = 0
    private var assemblyBuffer: Data = Data()
    private var pendingTotalParts: Int = 0
    private var partsReceivedCount: Int = 0
    
    func addPart(frameId: UInt32, partIndex: Int, totalParts: Int, data: Data) -> Data? {
        if frameId != pendingFrameId {
            pendingFrameId = frameId
            pendingTotalParts = totalParts
            assemblyBuffer = Data()
            partsReceivedCount = 0
        }
        
        // Android sequentially appends. We follow suit.
        // Protocol note: This assumes parts arrive in order. 
        // If out of order, a more complex buffer would be needed, 
        // but Android just appends.
        assemblyBuffer.append(data)
        partsReceivedCount += 1
        
        if partsReceivedCount == totalParts {
            let result = assemblyBuffer
            partsReceivedCount = 0
            return result
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
