using System;
using System.Windows.Forms;

interface LauncherWindow : IWin32Window
{
    void Post(Action action);
    void Minimize();
    void Close();
}
