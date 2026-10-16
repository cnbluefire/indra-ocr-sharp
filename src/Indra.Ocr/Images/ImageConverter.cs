using Indra.Ocr.Pipeline;

namespace Indra.Ocr.Images;

/// <summary>
/// The port of the image half of <c>document.convert</c>: every frame of an image file (a PNG has
/// one, a multi-page TIFF many) is one page, OCR'd whole.
/// </summary>
/// <remarks>
/// The reference's <c>Converter.convert</c> branches on the file's own bytes
/// (<c>pipeline.is_pdf</c>): a PDF goes to the text-layer/OCR path (<c>Indra.Ocr.Pdf.PdfConverter</c>)
/// and anything else is decoded and OCR'd. The two halves live in the two decoder projects — which
/// is why neither of them needs the other — and the caller dispatches with
/// <see cref="OrtOcr.IsPdf"/>.
/// </remarks>
public sealed class ImageConverter
{
    private readonly OrtOcr _ocr;

    public ImageConverter(OrtOcr ocr)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        _ocr = ocr;
    }

    /// <summary>
    /// <c>convert</c> for a multi-frame image file (a multi-page TIFF): (frame index, page) for
    /// every frame, capped at <paramref name="maxPages"/> (0: all). A frame that fails yields an
    /// empty page whose <c>source</c> is "error" and whose <c>note</c> says why; a file that
    /// cannot be decoded raises <see cref="InputError"/>.
    /// </summary>
    public IEnumerable<(int Index, OcrPage Page)> ConvertFrames(string path, int maxPages = 0)
    {
        if (!File.Exists(path))
        {
            throw new InputError($"{path}: no such file");
        }

        // The reference decodes the whole file and then slices to max_pages. Each frame owns its
        // pixels, so the list is held exactly like cv2.imreadmulti's and the frames go to the
        // pipeline one at a time.
        List<GrayImageData> frames = ImageFiles.ReadImages(path);
        int pages = maxPages <= 0 ? frames.Count : Math.Min(frames.Count, maxPages);
        for (int i = 0; i < pages; i++)
        {
            int index = i;
            yield return (index, SafePage.Read(() => _ocr.Call(frames[index])));
        }
    }

    /// <summary>
    /// The one page of a single-frame image file — the first frame of a multi-frame one
    /// (<see cref="ConvertFrames"/> for all of them). Null only when the file has no frame at
    /// all, which the decoder reports as an <see cref="InputError"/> anyway.
    /// </summary>
    public OcrPage? Convert(string path)
    {
        foreach ((int _, OcrPage page) in ConvertFrames(path, maxPages: 1))
        {
            return page;
        }

        return null;
    }
}
