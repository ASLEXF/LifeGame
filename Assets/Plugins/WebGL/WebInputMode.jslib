mergeInto(LibraryManager.library, {
  WebInput_PrefersTouch: function () {
    try {
      var maxTouch = 0;
      if (navigator && typeof navigator.maxTouchPoints === 'number')
        maxTouch = navigator.maxTouchPoints | 0;

      var coarse = false;
      var noHover = false;
      if (window.matchMedia) {
        coarse = window.matchMedia('(pointer: coarse)').matches;
        noHover = window.matchMedia('(hover: none)').matches;
      }

      var ua = (navigator && navigator.userAgent) ? navigator.userAgent : '';
      var mobileUA = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini|Mobile/i.test(ua);
      // iPadOS 13+ reports as Macintosh but exposes a multi-touch screen.
      var iPadOS = /Macintosh/i.test(ua) && maxTouch > 1;

      return (coarse || (noHover && maxTouch > 0) || mobileUA || iPadOS) ? 1 : 0;
    } catch (e) {
      return 0;
    }
  }
});
