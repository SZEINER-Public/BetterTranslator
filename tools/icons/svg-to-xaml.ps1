# Converts the BetterTranslator icon SVGs into Themes/Icons.xaml.
# Each SVG becomes one IconDefinition; each shape inside becomes one IconPart,
# so per-part stroke weights and fills survive the conversion untouched.
#
# An SVG in overrides/ replaces the set's file of the same name, and may also
# add an icon the set does not carry. That is how a defective source icon is
# corrected without hand-editing the generated XAML. See tools/icons/README.md.

param(
    [Parameter(Mandatory)]
    [string]$IconSet,
    [string]$Out = (Join-Path $PSScriptRoot '..\..\src\BetterTranslator.App\Themes\Icons.xaml')
)

$src  = $IconSet
$over = Join-Path $PSScriptRoot 'overrides'
$out  = $Out

function ToPascal([string]$name) {
  ($name -split '[-_]' | ForEach-Object {
    if ($_.Length -eq 0) { '' } else { $_.Substring(0,1).ToUpperInvariant() + $_.Substring(1) }
  }) -join ''
}

# SVG allows the two arc flags to run into the next number ("0 001.5 2.2").
# WPF's geometry parser does not, so space them out.
function FixArcs([string]$d) {
  $rx = [regex]'([Aa])\s*(-?[\d.]+)[\s,]+(-?[\d.]+)[\s,]+(-?[\d.]+)[\s,]+([01])[\s,]*([01])[\s,]*(-?[\d.]+)[\s,]+(-?[\d.]+)'
  return $rx.Replace($d, { param($m)
    "{0} {1} {2} {3} {4} {5} {6} {7}" -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value,
      $m.Groups[4].Value, $m.Groups[5].Value, $m.Groups[6].Value, $m.Groups[7].Value, $m.Groups[8].Value
  })
}

function Attr([string]$tag, [string]$name) {
  $m = [regex]::Match($tag, "$name\s*=\s*""([^""]*)""")
  if ($m.Success) { return $m.Groups[1].Value } else { return $null }
}

function PartAttrs([string]$tag) {
  $sw   = Attr $tag 'stroke-width'
  $cap  = Attr $tag 'stroke-linecap'
  $join = Attr $tag 'stroke-linejoin'
  $fill = Attr $tag 'fill'

  $bits = @()
  if ($fill -and $fill -ne 'none') { $bits += 'Filled="True"' }
  else {
    if ($sw)   { $bits += "StrokeThickness=""$sw""" }
    if ($cap)  { $bits += "Cap=""$((ToPascal $cap))""" }
    if ($join) { $bits += "Join=""$((ToPascal $join))""" }
  }
  return ($bits -join ' ')
}

$skip = @('app-icon','app-icon-tile','wordmark','wordmark-muted','unpin')

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
[void]$sb.AppendLine('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
[void]$sb.AppendLine('                    xmlns:sys="clr-namespace:System;assembly=System.Runtime"')
[void]$sb.AppendLine('                    xmlns:c="clr-namespace:BetterTranslator.App.Controls">')
[void]$sb.AppendLine()
[void]$sb.AppendLine('    <!-- GENERATED from the BetterTranslator icon set. Each icon keeps its own')
[void]$sb.AppendLine('         box size and per-part stroke weight: the weights were tuned per size so')
[void]$sb.AppendLine('         they read evenly at 100 percent, and normalising them would undo that. -->')
[void]$sb.AppendLine()
[void]$sb.AppendLine('    <FontFamily x:Key="FontFamilyCaptionGlyph">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>')
[void]$sb.AppendLine()

# One entry per icon name. Overrides are added second, so they win.
$sources = @{}
Get-ChildItem "$src\*.svg" | ForEach-Object { $sources[$_.BaseName] = $_.FullName }
if (Test-Path $over) {
  Get-ChildItem "$over\*.svg" | ForEach-Object { $sources[$_.BaseName] = $_.FullName }
}

$count = 0
$sources.Keys | Sort-Object | ForEach-Object {
  $stem = $_
  if ($skip -contains $stem) { return }

  $svg = (Get-Content $sources[$stem] -Raw) -replace "`r?`n", ''
  $key = 'Icon' + (ToPascal $stem)

  $w = [double](Attr $svg 'width')
  $h = [double](Attr $svg 'height')

  $box = "Box=""$w"""
  if ($h -ne $w) { $box += " BoxHeight=""$h""" }

  [void]$sb.AppendLine("    <c:IconDefinition x:Key=""$key"" $box>")

  foreach ($m in [regex]::Matches($svg, '<(circle|rect|path|line)\b[^>]*>')) {
    $tag  = $m.Value
    $kind = $m.Groups[1].Value
    $at   = PartAttrs $tag
    $open = if ($at) { "        <c:IconPart $at>" } else { '        <c:IconPart>' }

    switch ($kind) {
      'circle' {
        $cx = Attr $tag 'cx'; $cy = Attr $tag 'cy'; $r = Attr $tag 'r'
        [void]$sb.AppendLine($open)
        [void]$sb.AppendLine("            <EllipseGeometry Center=""$cx,$cy"" RadiusX=""$r"" RadiusY=""$r"" />")
        [void]$sb.AppendLine('        </c:IconPart>')
      }
      'rect' {
        $x = Attr $tag 'x'; $y = Attr $tag 'y'
        $rw = Attr $tag 'width'; $rh = Attr $tag 'height'; $rx = Attr $tag 'rx'
        $radius = if ($rx) { " RadiusX=""$rx"" RadiusY=""$rx""" } else { '' }
        [void]$sb.AppendLine($open)
        [void]$sb.AppendLine("            <RectangleGeometry Rect=""$x,$y,$rw,$rh""$radius />")
        [void]$sb.AppendLine('        </c:IconPart>')
      }
      'path' {
        $d = Attr $tag 'd'
        if (-not $d -or $d -eq 'currentColor') { return }
        $d = (FixArcs $d) -replace '&', '&amp;'
        [void]$sb.AppendLine($open)
        [void]$sb.AppendLine("            <PathGeometry Figures=""$d"" />")
        [void]$sb.AppendLine('        </c:IconPart>')
      }
      'line' {
        $x1 = Attr $tag 'x1'; $y1 = Attr $tag 'y1'; $x2 = Attr $tag 'x2'; $y2 = Attr $tag 'y2'
        [void]$sb.AppendLine($open)
        [void]$sb.AppendLine("            <LineGeometry StartPoint=""$x1,$y1"" EndPoint=""$x2,$y2"" />")
        [void]$sb.AppendLine('        </c:IconPart>')
      }
    }
  }

  [void]$sb.AppendLine('    </c:IconDefinition>')
  [void]$sb.AppendLine()
  $count++
}

[void]$sb.AppendLine('</ResourceDictionary>')
Set-Content -Path $out -Value $sb.ToString() -Encoding utf8
"wrote $count icons to $out"
