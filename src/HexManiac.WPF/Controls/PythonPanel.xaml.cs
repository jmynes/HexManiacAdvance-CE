using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System;
using System.Windows.Input;

namespace HavenSoft.HexManiac.WPF.Controls {
   public partial class PythonPanel {
      public PythonPanel() => InitializeComponent();

      private void PythonTextKeyDown(object sender, KeyEventArgs e) {
         if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) {
            e.Handled = true;
            if (DataContext is not PythonTool tool) return;
            tool.RunPython();
         } else if (e.Key == Key.Escape) {
            e.Handled = true;
            if (DataContext is not PythonTool tool) return;
            tool.Close();
         } else if (e.SystemKey == Key.Z && Keyboard.Modifiers == ModifierKeys.Alt) {
            // Notepad-style word wrap toggle. With Alt held, WPF reports e.Key as the
            // Key.System sentinel and puts the actual key in e.SystemKey instead.
            e.Handled = true;
            ((TextEditor)sender).ToggleWordWrap();
         }
      }

      private void ChangeInputTextSize(object sender, MouseWheelEventArgs e) {
         if (Keyboard.Modifiers != ModifierKeys.Control) return;
         var box = (TextEditor)sender;
         e.Handled = true;
         box.FontSize = (box.FontSize + Math.Sign(e.Delta)).LimitToRange(8, 30);
      }
   }
}
