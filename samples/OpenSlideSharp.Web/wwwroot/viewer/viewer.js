// OpenSeadragon viewer for the slide page, after openslide-python's slide-multipane.html, without jQuery.
(function () {
    'use strict';

    var view = document.getElementById('view');
    if (!view || typeof OpenSeadragon === 'undefined') {
        return;
    }

    var viewer = OpenSeadragon({
        id: 'view',
        prefixUrl: view.dataset.prefixUrl,
        timeout: 120000,
        animationTime: 0.5,
        blendTime: 0.1,
        constrainDuringPan: true,
        maxZoomPixelRatio: 2,
        minZoomImageRatio: 1,
        visibilityRatio: 1,
        zoomPerScroll: 2
    });
    viewer.scalebar({
        xOffset: 10,
        yOffset: 10,
        barThickness: 3,
        color: '#555555',
        fontColor: '#333333',
        backgroundColor: 'rgba(255, 255, 255, 0.5)'
    });

    function openSlide(url, mpp) {
        // The DZI is fetched from the server by the URL.
        viewer.open(url);
        viewer.scalebar({
            pixelsPerMeter: mpp ? (1e6 / mpp) : 0
        });
    }

    openSlide(view.dataset.url, parseFloat(view.dataset.mpp));

    document.querySelectorAll('a.load-slide').forEach(function (link) {
        link.addEventListener('click', function (event) {
            event.preventDefault();
            var current = document.querySelector('.current-slide');
            if (current) {
                current.classList.remove('current-slide');
            }
            link.parentElement.classList.add('current-slide');
            openSlide(link.dataset.url, parseFloat(link.dataset.mpp));
        });
    });
}());
