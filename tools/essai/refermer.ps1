# Referme (comme le bouton Annuler) les boites de dialogue de Windows que les essais font surgir quand il relit une cle Linux :
# "Microsoft Windows" (formater le disque ?) et "Emplacement non disponible". Ne touche a aucune autre fenetre.
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Menage {
  delegate bool Proc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(Proc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, Proc p, IntPtr l);
  public static List<string> Fermer(string[] titres) {
    var l = new List<string>();
    EnumWindows((h, p) => {
      if (!IsWindowVisible(h)) return true;
      var c = new StringBuilder(100); GetClassName(h, c, 100);
      var t = new StringBuilder(300); GetWindowText(h, t, 300);
      if (c.ToString() != "#32770" || Array.IndexOf(titres, t.ToString()) < 0) return true;
      var textes = new List<string>();
      EnumChildWindows(h, (e, q) => { var s = new StringBuilder(300); GetWindowText(e, s, 300); if (s.Length > 0) textes.Add(s.ToString()); return true; }, IntPtr.Zero);
      l.Add(t + " : " + string.Join(" | ", textes));
      PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
      return true;
    }, IntPtr.Zero);
    return l;
  }
}
'@
$f = [Menage]::Fermer(@('Microsoft Windows', 'Emplacement non disponible'))
if ($f.Count) { $f | ForEach-Object { "referme : $_" } } else { 'aucune boite de dialogue a refermer' }
