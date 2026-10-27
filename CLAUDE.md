# ArbiPache Development Instructions

## Versioning

The application version is defined once, as `<Version>` in `Directory.Build.props`.
Every requested change set bumps it with semantic versioning:

- Major feature or incompatible change: bump the major version (`4.0.0` to `5.0.0`).
- Minor feature: bump the minor version (`4.0.0` to `4.1.0`).
- Hotfix or bug fix: bump the patch version (`4.1.0` to `4.1.1`).

Make one bump per change set, based on its highest-impact change. Never duplicate the
version anywhere else; code reads it through `ArbiPache.Core.BuildInfo.Version`.

## Changelog

Maintain `CHANGELOG.md` whenever the application changes: newest version at the top,
release date, concise Added / Changed / Fixed entries, written for users.

## Commits

Conventional prefixes (`feat:`, `fix:`, `test:`, `chore:`, `docs:`). No `Co-Authored-By`
or AI attribution lines.

## Tests

- Unit: `dotnet test --project tests/ArbiPache.Core.Tests` and `dotnet test --project tests/ArbiPache.App.Tests`.
- Filter one class: append `-- --filter-class "*ClassName"`.
- Integration (real Apache/PHP/DB binaries): `dotnet test --project tests/ArbiPache.Integration.Tests`.
