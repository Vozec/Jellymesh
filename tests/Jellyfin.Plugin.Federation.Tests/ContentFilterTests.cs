using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class ContentFilterTests
{
    [Fact]
    public void No_filters_set_passes_anything()
    {
        Assert.True(ContentFilter.Passes(null, null, null, null, strictUnknown: false));
        Assert.True(ContentFilter.Passes(new[] { "anything" }, "R", null, null, strictUnknown: true));
    }

    [Fact]
    public void Blocked_tag_match_is_case_insensitive()
    {
        var blocked = new[] { "Kids" };
        Assert.False(ContentFilter.Passes(new[] { "kids", "comedy" }, null, blocked, null, false));
        Assert.False(ContentFilter.Passes(new[] { "KIDS" }, null, blocked, null, false));
        Assert.True(ContentFilter.Passes(new[] { "horror" }, null, blocked, null, false));
    }

    [Theory]
    [InlineData("G", "PG-13", true)]   // G under PG-13 cap
    [InlineData("PG", "PG-13", true)]  // PG under PG-13 cap
    [InlineData("PG-13", "PG-13", true)] // boundary inclusive
    [InlineData("R", "PG-13", false)]  // over cap
    [InlineData("NC-17", "R", false)]  // over cap
    [InlineData("TV-Y", "PG", true)]   // cross-system: TV → MPAA
    [InlineData("TV-MA", "PG-13", false)]
    public void Recognized_ratings_compared_numerically(string itemRating, string maxRating, bool expected)
    {
        Assert.Equal(expected, ContentFilter.Passes(null, itemRating, null, maxRating, strictUnknown: false));
    }

    [Theory]
    [InlineData("16+")]
    [InlineData("FSK-12")]
    [InlineData("BBFC-18")]
    [InlineData("M")]
    [InlineData("")]
    public void Unknown_rating_falls_open_when_strict_false(string itemRating)
    {
        Assert.True(ContentFilter.Passes(null, itemRating, null, "PG", strictUnknown: false));
    }

    [Theory]
    [InlineData("16+")]
    [InlineData("FSK-12")]
    [InlineData("BBFC-18")]
    [InlineData("")]
    public void Unknown_rating_is_hidden_when_strict_true(string itemRating)
    {
        // The bug fix: childproof admins set strictUnknown=true; regional ratings + unrated
        // items no longer silently leak through a max-rating filter.
        Assert.False(ContentFilter.Passes(null, itemRating, null, "PG", strictUnknown: true));
    }

    [Fact]
    public void Unparseable_max_rating_falls_open()
    {
        // Admin typo in MaxOfficialRating shouldn't lock peers out of everything.
        Assert.True(ContentFilter.Passes(null, "R", null, "BOGUS", strictUnknown: true));
    }

    [Fact]
    public void Tag_block_takes_precedence_over_rating()
    {
        // A G-rated item with a blocked tag is still hidden.
        Assert.False(ContentFilter.Passes(new[] { "trailer" }, "G", new[] { "trailer" }, "R", strictUnknown: false));
    }

    [Theory]
    [InlineData("g", 1)]
    [InlineData("PG", 5)]
    [InlineData("pg-13", 13)]
    [InlineData("R", 17)]
    [InlineData("nc-17", 18)]
    [InlineData("garbage", null)]
    public void ParentalScore_is_case_insensitive(string input, int? expected)
    {
        Assert.Equal(expected, ContentFilter.ParentalScore(input));
    }
}
