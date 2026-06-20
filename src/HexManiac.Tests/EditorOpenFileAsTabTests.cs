using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class EditorOpenFileAsTabTests : BaseViewModelTestClass {
      [Fact] public void OpenFileAsTab_AddsANewTab() {
         var editor = New.EditorViewModel();
         int before = editor.Count;
         // A non-.gba name builds a PokemonModel from arbitrary bytes (no ROM/Singletons tables needed).
         var tab = editor.OpenFileAsTab(new LoadedFile("scratch.bin", new byte[0x200]));
         Assert.NotNull(tab);
         Assert.Equal(before + 1, editor.Count);
         Assert.Same(tab, editor[editor.Count - 1]);
      }
      [Fact] public void OpenFileAsTab_NullFileReturnsNull() {
         var editor = New.EditorViewModel();
         int before = editor.Count;
         Assert.Null(editor.OpenFileAsTab(null));
         Assert.Equal(before, editor.Count);
      }
   }
}
