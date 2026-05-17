# ECDICT offline dictionary

Run this from the repository root to download and convert ECDICT:

```text
python tools/import_ecdict.py
```

The script writes:

```text
src/PopClip.App/plugins/dict/ecdict/ecdict.sqlite
```

Prebuilt SQLite files are also accepted:

- `ecdict.sqlite`
- `ecdict.db`
- `stardict.sqlite`
- `stardict.db`

Expected schema is the common ECDICT `stardict` table with columns such as
`word`, `phonetic`, `translation`, `definition`, `pos`, `exchange`, and `frq`.
