# Local Catalog Editor

Run `Start-Editor.ps1`, or start `Steamy.exe --catalog-editor`.



The catalog lives at `<Steamy data folder>/local-catalog/catalog.json`; its full location is shown in the editor. `STEAMY_LOCAL_CATALOG` can select a separate absolute catalog path. Saves are atomic, keep a `.bak` and use a cross-process write lock. Removing an entry keeps the original archive.


