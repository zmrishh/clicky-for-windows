// ── Global using aliases ─────────────────────────────────────────────────────
// Resolves ambiguities between WPF (System.Windows.*), WinForms
// (System.Windows.Forms), and System.Drawing that arise when both
// UseWPF=true and UseWindowsForms=true are set in the same project.

// IO types shadowed by System.Windows.Shapes.Path etc.
global using IoPath      = System.IO.Path;
global using IoFile      = System.IO.File;
global using IoDirectory = System.IO.Directory;
global using IoStream    = System.IO.Stream;
global using IoMemoryStream = System.IO.MemoryStream;
global using IoStreamReader = System.IO.StreamReader;

// WPF Application — avoids clash with System.Windows.Forms.Application
global using WpfApp   = System.Windows.Application;

// Colors
global using WpfColor     = System.Windows.Media.Color;
global using DrawingColor = System.Drawing.Color;

// Size
global using WpfSize     = System.Windows.Size;
global using DrawingSize = System.Drawing.Size;
