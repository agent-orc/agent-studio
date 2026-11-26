import Foundation
import CryptoKit
import Security

// Build once with swiftc and codesign, under the agent user's stable signing identity.
// dataRepresentation is an opaque device-bound wrapped blob, not a private scalar.
func run() throws {
    guard CommandLine.arguments.count == 3, SecureEnclave.isAvailable else {
        throw NSError(domain: "SecureEnclaveUnavailable", code: 1)
    }
    let file = URL(fileURLWithPath: CommandLine.arguments[1]).appendingPathComponent("device-key.enclave")
    let key: SecureEnclave.P256.Signing.PrivateKey
    if FileManager.default.fileExists(atPath: file.path) {
        key = try SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: Data(contentsOf: file))
    } else {
        key = try SecureEnclave.P256.Signing.PrivateKey()
        try key.dataRepresentation.write(to: file, options: .withoutOverwriting)
    }
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    func url64(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
    }
    let answer: [String: Any]
    if CommandLine.arguments[2] == "public" {
        // A wrapped Secure Enclave blob cannot be imported as a software private scalar.
        guard (try? P256.Signing.PrivateKey(rawRepresentation: key.dataRepresentation)) == nil else {
            throw NSError(domain: "UnexpectedPrivateKeyExport", code: 2)
        }
        let raw = key.publicKey.x963Representation
        answer = ["publicJwk": ["kty": "EC", "crv": "P-256", "x": url64(raw.subdata(in: 1..<33)), "y": url64(raw.subdata(in: 33..<65))], "nonExportable": true]
    } else if CommandLine.arguments[2] == "sign" {
        guard let data = Data(base64Encoded: FileHandle.standardInput.readDataToEndOfFile()) else {
            throw NSError(domain: "InvalidInput", code: 3)
        }
        answer = ["signature": try key.signature(for: data).rawRepresentation.base64EncodedString()]
    } else { throw NSError(domain: "InvalidOperation", code: 4) }
    FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: answer))
}
do { try run() } catch { fputs("Secure Enclave provider unavailable.\n", stderr); exit(1) }
