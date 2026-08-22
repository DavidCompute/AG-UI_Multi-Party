# Build a minimal valid .docx (OOXML zip) for KB upload testing.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-test-docx.ps1 -OutPath test.docx
param([string]$OutPath = "test.docx")

# NOTE: .NET's ZipFile.CreateFromDirectory writes BACKSLASH separators on Windows,
# which breaks OPC (requires forward slashes). Build entries explicitly instead.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$contentTypes = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>
'@

$rels = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
'@

$document = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>AGUI knowledge base docx test: deployment checklist for the project.</w:t></w:r></w:p>
    <w:p><w:r><w:t>Second paragraph: verify ports, database connection and embedding model.</w:t></w:r></w:p>
  </w:body>
</w:document>
'@

if (Test-Path $OutPath) { Remove-Item $OutPath -Force }

$fs = [System.IO.File]::Open($OutPath, [System.IO.FileMode]::Create)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $names = @{
        "[Content_Types].xml" = $contentTypes
        "word/document.xml"   = $document
        "_rels/.rels"         = $rels
    }
    foreach ($name in $names.Keys) {
        $entry = $zip.CreateEntry($name)
        $sw = New-Object System.IO.StreamWriter($entry.Open(), $utf8NoBom)
        $sw.Write($names[$name])
        $sw.Close()
    }
}
finally {
    $zip.Dispose()
    $fs.Dispose()
}
Write-Host "docx created: $OutPath ($((Get-Item $OutPath).Length) bytes)"
