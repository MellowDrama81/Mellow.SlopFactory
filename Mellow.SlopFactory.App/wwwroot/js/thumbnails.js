window.slopFactory = window.slopFactory || {};

// Scales an image down to fit maxSize x maxSize and returns it as JPEG bytes.
window.slopFactory.makeThumbnail = async function (bytes, contentType, maxSize) {
    const bitmap = await createImageBitmap(new Blob([bytes], { type: contentType || "image/png" }));
    try {
        const scale = Math.min(1, maxSize / Math.max(bitmap.width, bitmap.height));
        const width = Math.max(1, Math.round(bitmap.width * scale));
        const height = Math.max(1, Math.round(bitmap.height * scale));

        const canvas = new OffscreenCanvas(width, height);
        const context = canvas.getContext("2d");
        // JPEG has no alpha; put transparent images on white rather than black.
        context.fillStyle = "#fff";
        context.fillRect(0, 0, width, height);
        context.drawImage(bitmap, 0, 0, width, height);

        const blob = await canvas.convertToBlob({ type: "image/jpeg", quality: 0.85 });
        return new Uint8Array(await blob.arrayBuffer());
    } finally {
        bitmap.close();
    }
};
