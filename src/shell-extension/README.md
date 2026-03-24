# Shell Integration

This project builds a thin native Windows command client that forwards a selected file path to the running Instant File Share agent over a named pipe.

Current scope:

- native Win32 binary
- intended Explorer context-menu target
- no network, hashing, or share creation logic in the native process

The agent owns the actual share lifecycle and copies the resulting URL to the clipboard.
