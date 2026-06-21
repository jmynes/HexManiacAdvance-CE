using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   // Canonical slug / form mapping for ROM species names, so ROM data lines up with
   // external sources (PokeAPI). The tricky cases are punctuation, gender symbols,
   // and the multi-form species.
   public class PokemonSpeciesNamingTests {
      [Theory]
      [InlineData("BULBASAUR", "bulbasaur")]
      [InlineData("MR. MIME", "mr-mime")]      // punctuation collapses, still one mon
      [InlineData("NIDORAN\\sf", "nidoran-f")] // HMA female-symbol escape
      [InlineData("NIDORAN\\sm", "nidoran-m")] // HMA male-symbol escape
      [InlineData("NIDORAN♀", "nidoran-f")]    // raw unicode form too
      [InlineData("FARFETCH'D", "farfetchd")]
      [InlineData("HO-OH", "ho-oh")]
      [InlineData("PORYGON2", "porygon2")]
      [InlineData("DEOXYS", "deoxys")]
      public void Slug_Canonicalizes(string romName, string expected) =>
         Assert.Equal(expected, PokemonSpeciesNaming.Slug(romName));

      [Fact] public void Slug_GenderVariantsStayDistinct() =>
         Assert.NotEqual(PokemonSpeciesNaming.Slug("NIDORAN\\sf"), PokemonSpeciesNaming.Slug("NIDORAN\\sm"));

      [Fact] public void Slug_BlankIsNull() {
         Assert.Null(PokemonSpeciesNaming.Slug(""));
         Assert.Null(PokemonSpeciesNaming.Slug("   "));
      }

      [Fact] public void Forms_OnlyForMultiFormSpecies() {
         Assert.Equal(4, PokemonSpeciesNaming.FormsFor("deoxys").Length);
         Assert.Equal(4, PokemonSpeciesNaming.FormsFor("castform").Length);
         Assert.Null(PokemonSpeciesNaming.FormsFor("bulbasaur"));
         Assert.Null(PokemonSpeciesNaming.FormsFor("unown")); // single-form in PokeAPI
      }

      [Theory]
      [InlineData("BPRE0", "deoxys-attack")]   // FireRed
      [InlineData("BPGE0", "deoxys-defense")]  // LeafGreen
      [InlineData("BPEE0", "deoxys-speed")]    // Emerald
      [InlineData("AXVE0", "deoxys-normal")]   // Ruby
      public void DeoxysForme_VariesByGame(string code, string expected) =>
         Assert.Equal(expected, PokemonSpeciesNaming.DeoxysFormeFor(code));

      [Fact] public void DeoxysForme_UnknownGameIsNull() =>
         Assert.Null(PokemonSpeciesNaming.DeoxysFormeFor("ZZZZ0"));
   }
}
