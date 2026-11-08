using Android.Gms.Tasks;
using Android.Graphics;
using NightLoop.Engine.Screen;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;

namespace NightLoop.Mobile.Core;

/// <summary>Reads text with Google's on-phone text recognition (offline, nothing leaves the phone).</summary>
internal sealed class PhoneTextReader : ITextReader
{
    public static readonly PhoneTextReader Instance = new();

    private ITextRecognizer? _recognizer;

    public async Task<IReadOnlyList<TextLine>> ReadAsync(ScreenImage image, CancellationToken cancellationToken)
    {
        _recognizer ??= TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);
        var pixels = new int[image.Width * image.Height];
        var bgra = image.Bgra;
        for (int i = 0, j = 0; i < pixels.Length; i++, j += 4)
            pixels[i] = (255 << 24) | (bgra[j + 2] << 16) | (bgra[j + 1] << 8) | bgra[j];
        using var bitmap = Bitmap.CreateBitmap(pixels, image.Width, image.Height, Bitmap.Config.Argb8888!)!;
        var done = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recognizer.Process(InputImage.FromBitmap(bitmap, 0))
            .AddOnSuccessListener(new Listener(r => done.TrySetResult(r)))
            .AddOnFailureListener(new Failure(e => done.TrySetException(new InvalidOperationException(e.Message))));
        await using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
        {
            if (await done.Task is not Text text) return [];
            var lines = new List<TextLine>();
            foreach (var block in text.TextBlocks)
            foreach (var line in block.Lines)
            {
                var words = new List<TextWord>();
                foreach (var e in line.Elements)
                {
                    var r = e.BoundingBox;
                    if (r is null) continue;
                    words.Add(new TextWord(e.Text, new ScreenRect(image.Left + r.Left, image.Top + r.Top, r.Width(), r.Height())));
                }
                if (words.Count > 0) lines.Add(new TextLine(words));
            }
            return lines;
        }
    }

    private sealed class Listener(Action<Java.Lang.Object?> act) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result) => act(result);
    }

    private sealed class Failure(Action<Java.Lang.Exception> act) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception e) => act(e);
    }
}
