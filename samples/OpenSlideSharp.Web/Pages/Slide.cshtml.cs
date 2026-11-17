using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OpenSlideSharp.Web.Pages;

/// <summary>The viewer of one slide, with its associated images and properties.</summary>
public sealed class SlideModel(SlideSite site) : PageModel
{
    /// <summary>The slide to show.</summary>
    public SlideView Slide { get; private set; } = null!;

    /// <summary>Handles GET; the slide name is the path after <c>/slide/</c>.</summary>
    public IActionResult OnGet(string path)
    {
        SlideView? slide = site.Describe(path, Request);
        if (slide is null)
        {
            return NotFound();
        }

        Slide = slide;
        return Page();
    }
}
