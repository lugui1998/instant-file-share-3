# Browser Transfer Encryption Experiment

This prototype explores browser-side AES-GCM encryption for public share transfers. It is opt-in and is not the default download or receive-upload behavior.

## Download key delivery

Download decryption uses URL-fragment key delivery:

```text
#ifs-crypto=aes-gcm-v1&ifs-key=<base64url-256-bit-key>&ifs-mode=download
```

URL fragments are not included in HTTP requests, so the local agent, reverse proxy, and access logs do not receive the raw AES key through the request line. The public page parses the fragment in the browser and uses Web Crypto to decrypt the fetched ciphertext.

## Receive upload encryption

Receive uploads can use:

```text
#ifs-crypto=aes-gcm-v1&ifs-key=<base64url-256-bit-key>&ifs-mode=upload
```

When the receive page advertises the experiment and this fragment is present, the browser encrypts the selected file with AES-GCM before sending it. The current prototype stores ciphertext on the host (`store-encrypted`) and sends only non-secret metadata such as encryption mode, algorithm, IV, and plaintext size headers.

## Protections

- Protects uploaded file contents from a host that stores only ciphertext for opted-in receive uploads.
- Keeps fragment-delivered key material out of HTTP requests, query strings, reverse-proxy logs, and agent request logs.
- Uses AES-GCM authentication, so ciphertext tampering is rejected during browser decryption.

## Non-protections

- Does not protect data from JavaScript served by a compromised host page, browser extensions, malware, or a compromised client machine.
- Does not hide file names, sizes, timing, IP addresses, or transfer metadata.
- Does not make existing live file-reference shares encrypted by default.
- Does not yet provide a production key-sharing, key-rotation, sender identity, or recovery model.
- The download decrypt path assumes the fetched object is already AES-GCM ciphertext matching the fragment key and IV metadata; the host cannot encrypt a plaintext live file for the browser without receiving a key or using a separate sender-side encryption workflow.

## Nonce and IV rules

AES-GCM requires that an IV is never reused with the same key. This prototype derives each 96-bit IV as:

```text
4-byte random transfer nonce prefix || 8-byte big-endian chunk index
```

For a given key, the random transfer nonce prefix must be unique per encrypted transfer, and each chunk index must be used at most once. Reusing an IV with the same AES-GCM key can compromise confidentiality and authentication.
