import Foundation
import Security

public struct Keychain {
    private let service: String
    public init(service: String = "io.github.WanDa249.CPAplusQuota") { self.service = service }
    private func query(_ origin: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: origin, kSecAttrSynchronizable as String: false]
    }
    public func read(origin: String) throws -> String? {
        var query = query(origin)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        try check(status)
        guard let data = result as? Data, let key = String(data: data, encoding: .utf8) else { throw KeychainError(status: errSecDecode) }
        return key
    }
    public func save(_ key: String, origin: String) throws {
        guard Endpoint.validKey(key) else { throw APIError.invalidKey }
        let value = [kSecValueData as String: Data(key.utf8)]
        let status = SecItemUpdate(query(origin) as CFDictionary, value as CFDictionary)
        if status == errSecItemNotFound {
            var entry = query(origin).merging(value) { _, new in new }
            entry[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            try check(SecItemAdd(entry as CFDictionary, nil))
        } else { try check(status) }
    }
    public func delete(origin: String) throws {
        let status = SecItemDelete(query(origin) as CFDictionary)
        if status != errSecItemNotFound { try check(status) }
    }
    private func check(_ status: OSStatus) throws {
        if status != errSecSuccess { throw KeychainError(status: status) }
    }
}

public struct KeychainError: LocalizedError {
    public let status: OSStatus
    public var errorDescription: String? { "钥匙串操作失败（\(status)）。请解锁登录钥匙串后重试。" }
}
