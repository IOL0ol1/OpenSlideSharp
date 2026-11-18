using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OpenSlideSharp.Web.Pages;

/// <summary>The viewer of the one slide in single-slide mode, or the list of slides in directory mode.</summary>
public sealed class IndexModel(SlideSite site) : PageModel
{
    /// <summary>The slide to show, in single-slide mode.</summary>
    public SlideView? Slide { get; private set; }

    /// <summary>The slides to list, in directory mode.</summary>
    public IReadOnlyList<SlideNode> Slides { get; private set; } = [];

    /// <summary>Handles GET.</summary>
    public IActionResult OnGet()
    {
        if (site.IsSingle)
        {
            Slide = site.Describe(SlideSite.SingleName, Request);
            return Slide is null ? NotFound() : Page();
        }

        Slides = site.GetListing();
        return Page();
    }
}
