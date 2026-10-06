/** Firestore documents are capped at 1 MiB; an image is stored on its own and kept well under that. */
const maxBytes = 850_000;

/**
 * Shrinks a pasted or picked image to a JPEG data URL small enough for one Firestore document.
 * Schedules are mostly text, so the width is capped where text is still readable, then quality
 * steps down until it fits.
 */
export async function compressImage(file: Blob, maxWidth = 1600): Promise<string> {
  const bitmap = await createImageBitmap(file);
  let width = Math.min(maxWidth, bitmap.width);

  for (let attempt = 0; attempt < 6; attempt++) {
    const height = Math.round((bitmap.height * width) / bitmap.width);
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext("2d")!;
    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, width, height);
    ctx.drawImage(bitmap, 0, 0, width, height);

    for (const quality of [0.85, 0.75, 0.65]) {
      const url = canvas.toDataURL("image/jpeg", quality);
      if (url.length <= maxBytes) return url;
    }
    width = Math.round(width * 0.8);
  }

  throw new Error("The image is too large even after shrinking; try a smaller screenshot.");
}

/** Only images this app produced are shown: a data URL of a raster image, nothing else. */
export const isImageDataUrl = (s: unknown): s is string => typeof s === "string" && /^data:image\/(jpeg|png|webp);base64,[A-Za-z0-9+/=]+$/.test(s);
