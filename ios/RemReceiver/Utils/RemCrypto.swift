import Foundation
import CryptoKit

enum RemCrypto {
    static let keySalt = "RemSound.v1.audio-key".data(using: .utf8)!
    static let fingerprintSalt = "RemSound.v1.fingerprint".data(using: .utf8)!
    static let iterations = 100_000
    
    static let nonceBytes = 12
    static let tagBytes = 16
    
    // Default keys for empty password
    static let emptyKey: [UInt8] = [
        231, 254, 148, 233, 109, 124, 250, 108, 81, 186, 142, 21, 144, 245, 14, 55,
        226, 52, 229, 176, 179, 230, 98, 178, 75, 228, 138, 66, 97, 245, 156, 24
    ]
    
    // Note: In a real production app, PBKDF2 would be implemented via CommonCrypto 
    // or a lightweight Swift wrapper. Since this is a port, I'll provide the 
    // structure.
    
    static func decrypt(key: Data, data: Data) -> Data? {
        guard data.count >= nonceBytes + tagBytes else { return nil }
        
        let nonce = data.prefix(nonceBytes)
        let tag = data.subdata(in: nonceBytes..<(nonceBytes + tagBytes))
        let ciphertext = data.suffix(from: nonceBytes + tagBytes)
        
        do {
            let aesKey = SymmetricKey(data: key)
            let sealedBox = try AES.GCM.SealedBox(combined: nonce + ciphertext + tag)
            return try AES.GCM.open(sealedBox, using: aesKey)
        } catch {
            print("Decryption failed: \(error)")
            return nil
        }
    }
}
