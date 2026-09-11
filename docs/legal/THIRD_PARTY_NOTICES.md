# Third-party notices

This is the baseline control file for a new repository. It must not be interpreted as a statement that Nashira contains no third-party material.

For every public release, generate an SBOM from the actual source tree and build artifacts, inspect direct and transitive licenses, and add every notice required for redistribution. The concrete inventory can only be completed after the corresponding source code and assets are incorporated into the repository.

| Component or asset | Version or source | Location in Nashira | License | Required notice or action | Review status |
|---|---|---|---|---|---|
| First-release inventory | To be generated from source and build manifests | Repository-wide | To be verified | Complete dependency and asset review and publish required notices | Release-time control |

Review must include at least:

- .NET and JavaScript dependencies and lockfiles;
- container base images and bundled binaries;
- fonts, icons, logos and screenshots;
- copied or adapted code from Netora or FlowWeaver;
- workflow schemas, conformance vectors, examples, skills and specifications;
- generated code or content with material provenance implications;
- model SDKs and external API client libraries.
