# SignPath artifact configurations

These XML files are the artifact configurations used by the signed-release
workflow. They are kept in the repository so the signing policy can be reviewed
alongside the build scripts.

| File | SignPath purpose | Input artifact | Parameter |
|---|---|---|---|
| `app-artifact-config.xml` | Sign the application executable | `PixelForge.exe` | `product_version`, `file_version` |
| `installer-artifact-config.xml` | Sign the setup executable | `PixelForge-Setup.exe` | `version` |

The product and file versions in the configurations are restrictions. SignPath
rejects a signing request when the PE metadata does not match the supplied
values, which prevents an artifact built for one version from being signed as
another.

The installer is staged under the fixed name `PixelForge-Setup.exe` while it is
signed. The release workflow renames the signed file to
`PixelForge-Setup-<version>-x64.exe` afterwards.

See [docs/SIGNPATH.md](../../docs/SIGNPATH.md) for the account, project, policy,
and GitHub configuration steps.
