# Test archives

- `fix.7z`, `fix-password.7z` — made for these tests (py7zr); the second one is encrypted with the
  password `online-fix.me`, headers included.
- `Rar5.*.rar` — copied from the SharpCompress test suite
  (https://github.com/adamhathcock/sharpcompress, MIT License). The encrypted one uses the
  password `test`.

- `480.7z` and `480.rar` — generated metadata fixtures containing nested `480.lua`, `481_123.manifest`, and an executable that must never be installed. The 7z fixture is solid; the RAR fixture uses the stored RAR4 format. No game data or executable backend is included.

- `unsafe-metadata.rar` — generated stored RAR4 with a Windows traversal path. It must be rejected before import. `480.rar` also uses Windows path separators, to cover archives created by WinRAR.
