window.slopFactory = window.slopFactory || {};

// Paints a mask over an image on a canvas. The mask is kept in its own canvas at the image's full size, as white
// strokes on transparent; the visible canvas shows the image with the mask tinted red on top.
window.slopFactory.maskEditor = (function () {
    const maxUndo = 15;
    let state = null;

    function render() {
        const { context, image, mask, tint, cursor } = state;
        context.drawImage(image, 0, 0);

        // Tint: red wherever the mask is painted.
        const tintContext = tint.getContext("2d");
        tintContext.globalCompositeOperation = "copy";
        tintContext.drawImage(mask, 0, 0);
        tintContext.globalCompositeOperation = "source-in";
        tintContext.fillStyle = "#ff1f5a";
        tintContext.fillRect(0, 0, tint.width, tint.height);
        context.globalAlpha = 0.5;
        context.drawImage(tint, 0, 0);
        context.globalAlpha = 1;

        if (cursor) {
            context.lineWidth = Math.max(1, image.width / 400);
            context.strokeStyle = state.erasing ? "#fff" : "#ff1f5a";
            context.setLineDash(state.erasing ? [6, 4] : []);
            context.beginPath();
            context.arc(cursor.x, cursor.y, state.size / 2, 0, Math.PI * 2);
            context.stroke();
            context.setLineDash([]);
        }
    }

    // Pointer position in image pixels; the canvas is shown scaled down to fit.
    function toImage(event) {
        const rect = state.canvas.getBoundingClientRect();
        return {
            x: (event.clientX - rect.left) * state.canvas.width / rect.width,
            y: (event.clientY - rect.top) * state.canvas.height / rect.height,
        };
    }

    function strokeTo(point) {
        const maskContext = state.mask.getContext("2d");
        maskContext.globalCompositeOperation = state.erasing ? "destination-out" : "source-over";
        maskContext.strokeStyle = "#fff";
        maskContext.fillStyle = "#fff";
        maskContext.lineWidth = state.size;
        maskContext.lineCap = "round";
        maskContext.lineJoin = "round";
        const from = state.last || point;
        if (from.x === point.x && from.y === point.y) {
            maskContext.beginPath();
            maskContext.arc(point.x, point.y, state.size / 2, 0, Math.PI * 2);
            maskContext.fill();
        } else {
            maskContext.beginPath();
            maskContext.moveTo(from.x, from.y);
            maskContext.lineTo(point.x, point.y);
            maskContext.stroke();
        }
        state.last = point;
    }

    function saveUndo() {
        const { mask } = state;
        state.undo.push(mask.getContext("2d").getImageData(0, 0, mask.width, mask.height));
        if (state.undo.length > maxUndo) {
            state.undo.shift();
        }
    }

    function onPointerDown(event) {
        if (event.button !== undefined && event.button > 0) {
            return;
        }
        event.preventDefault();
        state.canvas.setPointerCapture(event.pointerId);
        saveUndo();
        state.drawing = true;
        state.last = null;
        strokeTo(toImage(event));
        state.cursor = toImage(event);
        render();
    }

    function onPointerMove(event) {
        const point = toImage(event);
        state.cursor = event.pointerType === "touch" ? null : point;
        if (state.drawing) {
            // Coalesced events give smooth lines on fast moves.
            const events = event.getCoalescedEvents ? event.getCoalescedEvents() : [event];
            for (const e of events.length ? events : [event]) {
                strokeTo(toImage(e));
            }
        }
        render();
    }

    function onPointerUp() {
        state.drawing = false;
        state.last = null;
    }

    function onPointerLeave() {
        state.cursor = null;
        render();
    }

    function onKeyDown(event) {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "z") {
            event.preventDefault();
            api.undo();
        }
    }

    function hasMask() {
        const { mask } = state;
        const data = mask.getContext("2d").getImageData(0, 0, mask.width, mask.height).data;
        for (let i = 3; i < data.length; i += 4) {
            if (data[i] > 0) {
                return true;
            }
        }
        return false;
    }

    async function toPngBytes(canvas) {
        const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
        return new Uint8Array(await blob.arrayBuffer());
    }

    const api = {
        // Shows the image on the canvas, with an existing mask (white = masked) if one is given.
        async open(canvas, imageBytes, contentType, maskBytes, size) {
            api.close();
            const image = await createImageBitmap(new Blob([imageBytes], { type: contentType || "image/png" }));
            canvas.width = image.width;
            canvas.height = image.height;
            const mask = document.createElement("canvas");
            mask.width = image.width;
            mask.height = image.height;
            const tint = document.createElement("canvas");
            tint.width = image.width;
            tint.height = image.height;

            if (maskBytes) {
                // Brightness becomes coverage: white is masked, black is not.
                const saved = await createImageBitmap(new Blob([maskBytes], { type: "image/png" }));
                const maskContext = mask.getContext("2d");
                maskContext.drawImage(saved, 0, 0, mask.width, mask.height);
                const pixels = maskContext.getImageData(0, 0, mask.width, mask.height);
                const d = pixels.data;
                for (let i = 0; i < d.length; i += 4) {
                    const coverage = d[i];
                    d[i] = d[i + 1] = d[i + 2] = 255;
                    d[i + 3] = coverage;
                }
                maskContext.putImageData(pixels, 0, 0);
                saved.close();
            }

            state = {
                canvas, context: canvas.getContext("2d"), image, mask, tint, undo: [],
                size: size || Math.round(Math.max(image.width, image.height) / 20), erasing: false,
                drawing: false, last: null, cursor: null,
            };
            canvas.addEventListener("pointerdown", onPointerDown);
            canvas.addEventListener("pointermove", onPointerMove);
            canvas.addEventListener("pointerup", onPointerUp);
            canvas.addEventListener("pointercancel", onPointerUp);
            canvas.addEventListener("pointerleave", onPointerLeave);
            window.addEventListener("keydown", onKeyDown);
            render();
            return { width: image.width, height: image.height, size: state.size };
        },

        setSize(size) {
            state.size = size;
            render();
        },

        setErasing(erasing) {
            state.erasing = erasing;
            render();
        },

        undo() {
            const previous = state && state.undo.pop();
            if (previous) {
                state.mask.getContext("2d").putImageData(previous, 0, 0);
                render();
            }
            return state ? state.undo.length : 0;
        },

        canUndo() {
            return state ? state.undo.length > 0 : false;
        },

        clear() {
            saveUndo();
            state.mask.getContext("2d").clearRect(0, 0, state.mask.width, state.mask.height);
            render();
        },

        invert() {
            saveUndo();
            const { mask } = state;
            const maskContext = mask.getContext("2d");
            const pixels = maskContext.getImageData(0, 0, mask.width, mask.height);
            const d = pixels.data;
            for (let i = 0; i < d.length; i += 4) {
                d[i] = d[i + 1] = d[i + 2] = 255;
                d[i + 3] = 255 - d[i + 3];
            }
            maskContext.putImageData(pixels, 0, 0);
            render();
        },

        // The mask as a PNG at the image's full size, white where painted on black; null if nothing is painted.
        async exportMask() {
            if (!hasMask()) {
                return null;
            }
            const { mask } = state;
            const out = document.createElement("canvas");
            out.width = mask.width;
            out.height = mask.height;
            const outContext = out.getContext("2d");
            outContext.fillStyle = "#000";
            outContext.fillRect(0, 0, out.width, out.height);
            outContext.drawImage(mask, 0, 0);
            return await toPngBytes(out);
        },

        // A small see-through red version of the mask for showing over a thumbnail.
        exportOverlay(maxSize) {
            const { tint, mask } = state;
            const scale = Math.min(1, maxSize / Math.max(mask.width, mask.height));
            const out = document.createElement("canvas");
            out.width = Math.max(1, Math.round(mask.width * scale));
            out.height = Math.max(1, Math.round(mask.height * scale));
            const outContext = out.getContext("2d");
            outContext.globalAlpha = 0.5; // As see-through as in the editor.
            outContext.drawImage(tint, 0, 0, out.width, out.height);
            return out.toDataURL("image/png");
        },

        close() {
            if (!state) {
                return;
            }
            const { canvas } = state;
            canvas.removeEventListener("pointerdown", onPointerDown);
            canvas.removeEventListener("pointermove", onPointerMove);
            canvas.removeEventListener("pointerup", onPointerUp);
            canvas.removeEventListener("pointercancel", onPointerUp);
            canvas.removeEventListener("pointerleave", onPointerLeave);
            window.removeEventListener("keydown", onKeyDown);
            state.image.close();
            state = null;
        },
    };

    return api;
})();
