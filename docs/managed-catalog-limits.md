# Managed catalog response budgets

Managed publishing V1 returns a complete snapshot, including technical metadata for every variant. Catalog size is not the same as the size of a capability, status or callback response.

The plugin reads successful catalog responses directly from a byte-counting stream into the catalog model. It does not first retain a full byte buffer and a second UTF-16 JSON string. The model itself remains in memory until validation and reconciliation finish.

## Hard bounds

- Successful catalog response: at most 256 MiB of bytes actually read. A larger `Content-Length` fails before reading; missing or inaccurate length headers do not bypass the counting stream.
- Capability/panel, callback and catalog-conflict response bodies: the existing 16 MiB limit.
- Catalog items, including nested episodes: at most 50,000, aligned with the independent output-file budget.
- Planned output files: still at most 50,000 across the full snapshot. A movie with an NFO and one STRM consumes two files, so an otherwise valid snapshot can reach this limit before the item limit.
- Generated output: at most 1 MiB per file and 64 MiB in aggregate per mapping generation. The aggregate includes stale owned files retained by `cleanup=keep`. Oversized output is rejected before writing, preserving any active generation. The former 8 MiB aggregate budget could reject valid large catalogs after their complete response had already passed validation; the independent file-count and per-file bounds are unchanged.
- Existing mapping, variant, metadata-field, path and identity checks remain in force. Increasing the response budget does not relax these checks.
- The existing linked 30-second request timeout and caller cancellation include body reading.

Deserialization must read to EOF. Trailing whitespace is valid; trailing JSON documents or other non-whitespace are rejected. The plugin validates the entire catalog before publishing any mapping. It never treats a prefix, truncated response or over-budget snapshot as a successful full snapshot.

An installation beyond these bounds needs a separately designed catalog pagination/streaming protocol; repeatedly increasing constants is not a substitute for that protocol.
