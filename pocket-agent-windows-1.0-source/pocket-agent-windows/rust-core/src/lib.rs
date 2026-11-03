//! Rust MCP JSON-RPC engine, ported from the original Android project's core.
//! Windows-specific tools are implemented in the managed Windows host.
//!
//! The C ABI in `windows_ffi` uses only portable types, so it is compiled on every platform:
//! the Windows app loads `pocket_agent_core.dll`, and the tests (Rust and .NET) exercise the
//! same entry points on Linux through `libpocket_agent_core.so`.
mod protocol;

pub mod windows_ffi;
