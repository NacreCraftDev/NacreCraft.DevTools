# Compile advice acceptance project

This project is the acceptance fixture for the compile-error advice that
`pit build` prints. Every case fails on purpose: the project does not compile,
and that is the point.

The expectation for each case sits next to the broken line:

- `//expect: ...` the output must contain this text
- `//expect-not: ...` the output must not contain this text

## Running

```powershell
./verify.ps1
```

Or run pit directly and read the output:

```powershell
pit build --check-only --no-manifest --plain
```

`pit build` exits with code 1 because the project has errors. `verify.ps1`
checks the text instead of the exit code.

## Layout

| File | Covers |
| --- | --- |
| `Cases/Names.cs` | shared types, no errors |
| `Cases/MissingUsing.cs` | CS0246 and CS0103 where a namespace supplies the type |
| `Cases/MissingType.cs` | CS0246 near miss, and CS0246 with no candidate |
| `Cases/MissingName.cs` | CS0103 where a nearby member is the only candidate |
| `Cases/MissingMember.cs` | CS1061 and CS0117 member advice, CS0120 |
| `Cases/ArgumentCount.cs` | CS7036, CS1729 and CS1739 |
| `Cases/UnsafeCode.cs` | CS0227 |
| `Cases/GenericFallback.cs` | errors served by the static advice table |

## Conventions

- This is a plain csproj, not an ncproj. The `Build/kernel/` hint must not
  appear for missing types here; it belongs to ncproj projects only.
- A case that expects a diff also expects the exact wording. When the advice
  text changes, update the comment in the same change.
- Adding a case means adding a line pair: the broken code and its `//expect:`.

## Check environment

`pit build` checks a plain csproj with the template defaults, which enable
implicit usings and nullable and leave unsafe off. The csproj here disables
implicit usings so `dotnet build` would behave the same way, but pit does not
read that switch, so the cases only rely on namespaces outside the implicit
set (`System.Text`, `System.Text.RegularExpressions`).

## Why it is not part of the product build

`NacreCraft.Pit` never references this project and no solution
includes it. It exists to be run by hand or by `verify.ps1`.
