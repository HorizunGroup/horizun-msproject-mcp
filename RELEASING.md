# Releasing

Every public release is signed with the Certum certificate of HORIZUN GROUP S.A.S. before anyone can
install it, and nothing unsigned ever reaches NuGet. Published GitHub releases are immutable, so a
mistake is fixed with a new patch version, never by replacing assets.

## Flow

1. Bump the version everywhere it is declared (see `CONTRIBUTING.md`, "Releasing"), add the entry to
   `CHANGELOG.md`, open a PR, wait for green CI and merge.
2. Tag the merge: `git tag -a v1.6.1 -m "v1.6.1" && git push origin v1.6.1`.
3. `release.yml` (workflow name `publish to nuget`; neither name may change, NuGet Trusted Publishing is
   pinned to them) runs job `publish` on `windows-latest`: build, every test suite, `dotnet pack`, the
   `.mcpb`, and a **draft** GitHub release with the unsigned `.nupkg` and `.mcpb` attached. Nothing is
   pushed to NuGet here.
4. Job `firmar-y-publicar` then waits for approval of the `firma-certum` environment (required reviewer:
   pablo-horizun). GitHub notifies the reviewer.
5. Pablo connects **SimplySign Desktop** on the machine that hosts the runner, opens the run in the Actions
   tab and chooses **Review deployments -> Approve** for `firma-certum`.
6. The job, on the self-hosted runner (`[self-hosted, windows, horizun-sign]`):
   - runs `scripts/firmar-release.ps1 -SinPublicar -Salida <dir>`: signs the project's own DLL inside the
     `.nupkg` (the `.mcpb` carries only JavaScript and JSON, nothing to sign), regenerates hashes, verifies
     each signature and Certum timestamp, uploads the signed assets to the draft with `--clobber` and
     re-downloads them to compare;
   - authenticates with NuGet through Trusted Publishing and pushes the **signed** `.nupkg` from `<dir>`
     (`--skip-duplicate`);
   - publishes the release (`gh release edit --draft=false`).
7. `registry.yml` (`workflow_run` on `publish to nuget`) fires when the whole workflow, signing job
   included, completes successfully, waits for NuGet to index the package and lists it in the MCP Registry.
   If the signing job is rejected or fails, the registry workflow does not run.

If any signing step fails, the release stays a draft and NuGet is untouched.

## Hashes

No file in this repository pins the hash of a release asset. The `.mcpb` launcher installs the tool by
version from NuGet, and the signed `.nupkg` is what NuGet serves. Anything that pins a hash of the
`.nupkg` (for example the Horizun Hub Suite) must take it from the **signed** package published on
NuGet / the release, not from the CI build.

## The signing runner

The runner is installed under `C:\Users\pablo\.horizun\sign-runners\horizun-msproject-mcp` and started by
the scheduled task **Horizun Sign Runners**, which runs
`C:\Users\pablo\.horizun\sign-runners\iniciar-runners.ps1`. To check it: Settings -> Actions -> Runners in
the repository must show `horizun-sign` as *Idle*/*Online*; otherwise run the script (or the task) in a
normal PowerShell as the same Windows user. The runner must run as that user, because the certificate
lives in `Cert:\CurrentUser\My` (thumbprint `D19F95C700EFEB78F03B6A2F9F6FBF87BF9BF8FA`).

## If the signing job fails because SimplySign is disconnected

The script signs a canary file before touching the release and fails with "No se puede firmar
(SimplySign desconectado...)". Nothing was uploaded or published. Reconnect SimplySign Desktop (enter the
code yourself), then in the run choose **Re-run failed jobs** and approve the environment again.

If the job failed *after* pushing to NuGet (for example while publishing the release), re-running is safe:
the push uses `--skip-duplicate` and the script signs only what is not already signed with our certificate.

El runner no ejecuta signtool directamente: solicita la firma al servicio local por cola y verifica Authenticode y el sello Certum antes de publicar.

