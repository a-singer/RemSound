import Foundation
import CommonCrypto
import CryptoKit

class PcmFrameAssembler {
    private var pendingFrameId: UInt32 = 0
    private var assemblyBuffer: Data = Data()
    private var pendingTotalParts: Int = 0
    private var partsReceived: Set<Int> = []
    private var maxWriteEnd: Int = 0

    // Must match RemPacket.MaxAudioPayloadBytes on the Windows sender.
    private let chunkSize = 1454

    func addPart(frameId: UInt32, partIndex: Int, totalParts: Int, data: Data) -> Data? {
        if frameId != pendingFrameId {
            pendingFrameId = frameId
            pendingTotalParts = totalParts
            assemblyBuffer = Data(repeating: 0, count: chunkSize * max(totalParts, 1))
            partsReceived.removeAll()
            maxWriteEnd = 0
        }

        guard totalParts > 0, partIndex < totalParts else { return nil }

        let offset = partIndex * chunkSize
        let writeEnd = offset + data.count

        if assemblyBuffer.count < writeEnd {
            assemblyBuffer.append(Data(repeating: 0, count: writeEnd - assemblyBuffer.count))
        }
        assemblyBuffer.replaceSubrange(offset..<writeEnd, with: data)
        partsReceived.insert(partIndex)
        if writeEnd > maxWriteEnd { maxWriteEnd = writeEnd }

        if partsReceived.count == totalParts {
            let finalData = Data(assemblyBuffer.prefix(maxWriteEnd))
            partsReceived.removeAll()
            maxWriteEnd = 0
            return finalData
        }

        return nil
    }
}

enum RemCrypto {
    static let keySalt = "RemSound.v1.audio-key".data(using: .utf8)!
    static let fingerprintSalt = "RemSound.v1.fingerprint".data(using: .utf8)!
    static let iterations: UInt32 = 100_000
    static let keyLength = 32
    
    static let emptyKey: [UInt8] = [
        231, 254, 148, 233, 109, 124, 250, 108, 81, 186, 142, 21, 144, 245, 14, 55,
        226, 52, 229, 176, 179, 230, 98, 178, 75, 228, 138, 66, 97, 245, 156, 24
    ]
    
    static func deriveKey(password: String) -> Data? {
        return pbkdf2(password: password, salt: keySalt)
    }
    
    static func fingerprint(password: String) -> Data? {
        return pbkdf2(password: password, salt: fingerprintSalt)
    }
    
    private static func pbkdf2(password: String, salt: Data) -> Data? {
        if password.isEmpty && salt == keySalt { return Data(emptyKey) }
        
        var derivedKey = Data(count: keyLength)
        guard let passwordData = password.data(using: .utf8) else { return nil }
        
        let status = derivedKey.withUnsafeMutableBytes { (derivedBytes: UnsafeMutableRawBufferPointer) in
            passwordData.withUnsafeBytes { (passwordBytes: UnsafeRawBufferPointer) in
                salt.withUnsafeBytes { (saltBytes: UnsafeRawBufferPointer) in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        passwordBytes.baseAddress?.assumingMemoryBound(to: Int8.self),
                        passwordData.count,
                        saltBytes.baseAddress?.assumingMemoryBound(to: UInt8.self),
                        salt.count,
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
        guard data.count >= 28 else { return nil }
        
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
