# Shell Integration

This project builds a thin native Windows command client that forwards a selected file or folder path to the running Instant File Share agent over a named pipe.

Current scope:

- native Win32 binary
- intended Explorer context-menu target
- supports separate file, folder ZIP, and folder browse commands
- no network, hashing, or share creation logic in the native process

The agent owns the actual share lifecycle and copies the resulting URL to the clipboard.
