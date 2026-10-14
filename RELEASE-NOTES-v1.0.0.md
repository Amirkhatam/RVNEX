# RVNEX v1.0.0

## First release

RVNEX v1.0.0 provides a local Windows workspace for VMware inventory collection and report analysis.

- Collect an RVTools XLSX workbook from a saved vCenter or ESXi profile.
- Validate the generated workbook before adding it to the local report history.
- Import and inspect existing RVTools workbooks.
- Compare report snapshots and export comparison results.
- Store optional connection credentials through Windows Credential Manager.
- Choose Light, Dark, or System theme.
- Use RVNEX logo branding, About information, and a startup screen.

## Requirements and limitations

- Windows x64, build 17763 or later; deployment uses MSIX and includes the x64 Microsoft Windows App Runtime 2.5.1 framework dependency.
- RVTools 4.5 or later must be installed separately for live collection.
- The prepared local MSIX is unsigned. It requires signing with the approved publisher certificate and must be tested on a clean target machine before production distribution.
- RVNEX does not bundle or redistribute RVTools.
