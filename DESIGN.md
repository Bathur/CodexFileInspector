# Final design and tool contract

## Purpose and implementation

Codex File Inspector supplies four predictable read-only filesystem primitives for normal coding tasks. It does not try to replace Shell for builds/Git, add a permission system, or become an IDE.

- C# targeting `net10.0-windows`; official ModelContextProtocol SDK 2.2.0; STDIO transport.
- Official bundled ripgrep 15.2.0, launched by explicit install-relative path and argv, without a Shell, runtime download, user ripgrep config, or `--follow`.
- Stable traversal/pagination, cancellation propagation, bounded pipe processing, and Windows Job Object cleanup.
- Current and initialize-era MCP compatibility. Codex's per-server exposure setting keeps the four tools direct-only while leaving Code Mode available to other tools.
- A small managed filesystem/process boundary isolates Windows-specific path and lifecycle details. This distribution does not claim cross-platform support.

## Representation and exposure rationale

- `read_file.content` contains no synthetic `L<number>: ` prefixes, so quoting text or preparing patch context does not require removing them. Structured start/end fields retain the line range; `grep` keeps numbered blocks for navigation. This separates text reuse from location-oriented search without claiming byte-preserving reads.
- Direct-only exposure is a Codex Host configuration choice, not a different MCP transport. The server still returns both MCP representations described below; the direct tool path avoids asking the model to select and re-render them in an outer JavaScript call, with an additional shared output budget. Independent `read_file` calls may run concurrently when supported by the Host; direct-only does not impose a sequential-only rule. Other tools can still use Code Mode.

## Shared rules

Concrete path inputs are fully qualified Windows absolute paths; returned paths are normalized logical absolute paths reusable in later calls. UNC and mapped paths are allowed, without trying to classify storage as local, remote, or cloud. There is no physical-target resolver or duplicate requested/resolved path pair. The OS determines access.

Each call supplies one complete object-valued `structuredContent` and one TextContent block containing the same compact JSON. Both representations are individually bounded at 32 KiB UTF-8. The server also declares an output schema; host presentation of that schema is host-dependent.

| Status | Meaning |
| --- | --- |
| `success` | Completed query or a bounded page with valid continuation. Empty results are successes. |
| `partial` | Complete evidence is retained with bounded warnings, but traversal or other soft failures prevent a complete claim; totals, `has_more`, and continuation are omitted. |
| `error` | Typed failure with code, message, and retryability; not an ordinary zero-match result. |

Totals are exact when present. Missing totals mean unknown, not zero. Continuation repeats traversal and does not provide a cross-call snapshot. An unchanged query can have different results when the filesystem changes. `path_missing` is not blindly retryable; correct or rediscover the path first.

No tool takes a timeout argument and the server has no hard-coded call deadline. The documented Codex configuration sets 300 seconds per call. The server owns cancellation and process/handle cleanup, but a cancelled Host request may never receive a result envelope.

## `read_file`

- One file; 1-based `start_line` defaults to 1; `line_count` defaults to 200 and is capped at 2,000.
- Returns decoded logical text with no injected line numbers or numbering switch. This is not a byte-preserving transfer API: decoding, logical newline handling, and explicit limits apply.
- Empty files succeed; a non-empty requested range starting past EOF is a typed error. `total_lines` appears only when known without extra full-file work.
- `has_more` describes later logical lines; `next_start_line` continues a bounded read. `line_truncations` separately marks text clipped within a returned line. Each visible line excerpt is bounded at 4 KiB UTF-8.
- Strict UTF-8 and supported BOM-marked UTF-16 decoding; no code-page guessing or media decoding. Material changes or decoding failures do not return mixed partial file content.

## `grep`

- One file or directory, a non-empty single-line pattern, and explicit `literal` or `regex`. Content matching is case-sensitive by default; no smart-case, multiline, PCRE2, or CLI aliases.
- `matches`: paginate matching logical lines, not submatches, context lines, or merged blocks. A line containing two occurrences still consumes one result unit.
- `files_with_matches`: paginate distinct matching files.
- `count`: paginate per-file count records while `totals` describes the complete search, not just that page. Matching files, matching lines, and occurrences are distinct counts.
- `max_results` defaults to 100 and is capped at 1,000; offsets are capped at 1,000,000.
- Symmetric context of 0–20 lines is available only in matches mode. Select the page's match lines first, then merge overlapping or adjacent context in the same file. Blocks contain numbered `content` plus `match_lines` and occurrence counts; merging does not change pagination units.
- Match/context excerpts are bounded at 4 KiB. When context exceeds the result budget, retain selected matches, drop farthest optional context, rebuild contiguous blocks, and mark `context_truncated`. Only then remove trailing match units if needed, with valid continuation.

## `glob` and recursive filters

- `glob` requires a directory and non-empty `include_globs`, producing a deduplicated, stable set of absolute file paths. It does not return directories or file metadata.
- `max_results` defaults to 200 and is capped at 2,000; offsets are capped at 1,000,000. Path-glob matching defaults to case-insensitive on Windows.
- Both search tools interpret include/exclude patterns relative to the supplied search root, or the parent of a directly named grep file. Use `/` in patterns. Exact `Character/file.cpp` does not implicitly mean `**/Character/file.cpp`; the latter also selects deeper paths.
- Reject absolute, backslash-separated, `..`-traversing, empty, and leading-`!` patterns. Use `exclude_globs` for exclusions. Multiple includes form a union; explicit excludes win.
- Use native ripgrep precedence: explicit include globs and loaded ignore-file allow rules may override default hidden filtering. On Windows, hidden covers dot names and the Hidden attribute. Matching a hidden file does not necessarily whitelist its hidden parent directory.
- `include_hidden=true` disables default hidden filtering, not ignore/exclude rules. `respect_ignore_files=false` disables ignore-file rules, including allow rules, without implying `--hidden`.
- Applicable project and same-repository parent ignore rules are respected by default; global ignore configuration is disabled. Recursive traversal never enables link following; named roots use normal OS behavior.

## `list_directory`

Return every OS-enumerable direct child, including hidden and ignored entries, in stable ordinal name order. Each entry has exact name, logical absolute path, and `file`, `directory`, `link`, or `other` kind. There is no recursion, filtering, target resolution, or detailed stat output.

`max_entries` defaults to 200 and is capped at 2,000. Offset plus requested entries must not exceed 100,000; scanning is capped at 1,000,000 direct children. Enumeration/change failures cannot claim complete totals or reliable continuation.

## Optional failure diagnostics

The `--diagnostics` startup argument opts into internal JSONL logging; it is disabled by default and is not a tool parameter. Only a final standard `error` or `partial` result qualifies. `success`, including empty results and normal budget truncation, skips log construction and file access. Calls that fail before a standard result exists, ordinary cancellation, startup/transport failures, and forced termination are outside this capture boundary. An already-caught exception converted into a standard `internal_error` is in scope.

The sink uses `Path.Combine(AppContext.BaseDirectory, "logs")`, not the Host working directory, inspected root, or `dotnet.exe` directory. It creates the directory only when the first eligible record is written. Each process/run has uniquely named files and one background writer. The internal `logs` directory itself must not be a link; if it is, the sink stops. This diagnostic-location guard does not change OS-delegated link behavior for tool inputs.

Records contain time, server version, process/record identity, tool name, method-bound arguments, the existing error/warnings, and exception type/message/stack when already available at the tool boundary. Bound arguments include applied defaults and are not raw JSON-RPC requests. No read content, search excerpts, returned file collections, full result, raw ripgrep stderr, or newly collected deep service diagnostics are copied. Existing error and warning limits still apply. Paths, expressions, and exception details are not redacted.

| Resource | Initial fixed limit | Behavior |
| --- | ---: | --- |
| Pending records | 32 | Nonblocking enqueue; discard the new record when full |
| Encoded record | 256 KiB | Keep valid JSON and explicit field-truncation evidence |
| Per-process file | 8 MiB | Rotate before the next record would exceed the limit; never split a record |
| Diagnostic directory | 128 MiB | Soft retention target across this installation's diagnostic files |

Argument and exception strings share a 160 KiB encoded-data budget within the total record limit, leaving space for existing standard diagnostics and truncation metadata. The encoder prioritizes an argument identified by the error. Oversize strings retain marked head/tail fragments where space permits; each shortened field reports original and retained lengths in UTF-16 code units. Glob arrays retain at most 64 entries each, with omitted item counts; the specifically identified offending item is prioritized separately. Exception chains retain at most four exception objects, including the outer exception, and mark deeper omission. These limits can lose reproduction evidence, but the JSON remains valid and omissions are explicit. The existing 4 KiB inspection-line display limit can also clip an intact JSONL record when it is read later.

Only bounded encoded bytes enter the queue: pending payloads occupy at most about 8 MiB, plus queue/runtime overhead and any record being constructed or written. Non-success calls incur serialization work but do not wait for disk I/O, queue space, rotation, or shutdown draining. The writer uses ordinary asynchronous flushing without guaranteed physical-disk durability. A full queue, process exit, or file-writing failure may lose records; a crash can leave the final line incomplete. A writing failure disables the sink for that process, discards pending records, and leaves tool results unchanged. Restarting the server allows another attempt.

Retention runs when opening the first or next file. It recognizes only this application's exact diagnostic filename pattern and regular, non-link files. Closed files are considered oldest `LastWriteTimeUtc` first, with ordinal filename order as the tie-breaker. Active files cannot be deleted through their open handles; unrelated files remain untouched. Active files, deletion failures, and growth between cleanups can keep usage above the soft target. There is no periodic sweeper, strict cross-process quota, or automatic cleanup of another installation's directory.

The four tool APIs, canonical result schemas/budgets, and annotations remain unchanged: `readOnlyHint=true`, `destructiveHint=false`, `idempotentHint=true`, and `openWorldHint=false`. These describe the requested inspection operation, which never mutates target data. With diagnostics enabled, replaying a non-success may append another internal record and trigger retention in the fixed log directory. That internal diagnostic write is the narrow exception to having no side effects; it does not add a file-mutation tool or permission system.

## Deliberately omitted

No multi-file batch reader, standalone metadata tool, media reader, semantic search, file-mutation tool, or arbitrary process tool. Independent reads can be called concurrently. Unknown-length log tails and arbitrary later segments of clipped long lines remain consumer-policy exceptions outside this basic tool surface.

These choices reflect a bounded personal tool, not a claim that every agent should use an identical schema. The test suite includes real-engine boundary cases and actual STDIO metadata checks; passing it is not proof of bug-free behavior or independent security review.
