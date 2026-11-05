# Book export, phase 0 spike

Throwaway harness behind the findings in `docs/BOOK_EXPORT.md` section 9. It is **not** part of the product and nothing
references it. It builds a synthetic book (gazetteer, stat blocks, masks, Hebrew and Russian pages), loads the app's real
fonts through a Playwright route (every other request aborted, JavaScript off: the closed-network design), and prints a PDF.

```sh
node tools/book-spike/gen.js 15 > /tmp/book.html     # argument: size multiplier
node tools/book-spike/render.js /tmp/book.html /tmp/book.pdf
pdffonts /tmp/book.pdf; pdfimages -list /tmp/book.pdf; pdfinfo /tmp/book.pdf
```

Needs Node, the `playwright` package with a Chromium, and poppler-utils for the inspection commands. Results were taken on
Chromium 153.0.8010.12 (Linux); re-run on the Chromium you intend to support.
