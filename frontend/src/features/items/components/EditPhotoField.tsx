import { useRef, useState } from "react";
import { Camera, X, Loader2 } from "lucide-react";
import { ALLOWED_PHOTO_TYPES } from "../schemas/reportLostItemSchema";
import { resolvePhotoUrl } from "../../../config/env";
import type { ItemPhoto } from "../api/reportLostItem";

type EditPhotoFieldProps = {
  photo: ItemPhoto | null;
  onReplace: (file: File) => Promise<void>;
  onDelete: () => Promise<void>;
};

export function EditPhotoField({ photo, onReplace, onDelete }: EditPhotoFieldProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [isBusy, setIsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleFile = async (file: File) => {
    setError(null);
    if (!ALLOWED_PHOTO_TYPES.includes(file.type)) {
      setError("Please choose a JPEG, PNG, or WEBP image.");
      return;
    }
    setIsBusy(true);
    try {
      await onReplace(file);
    } catch {
      setError("Couldn't upload that photo. Please try again.");
    } finally {
      setIsBusy(false);
    }
  };

  const handleDelete = async () => {
    setError(null);
    setIsBusy(true);
    try {
      await onDelete();
    } catch {
      setError("Couldn't remove the photo. Please try again.");
    } finally {
      setIsBusy(false);
    }
  };

  if (photo) {
    return (
      <div>
        <div className="relative mx-auto aspect-square w-40 overflow-hidden rounded-xl border border-gray-200 bg-gray-100">
          <img src={resolvePhotoUrl(photo.url)} alt="" className="h-full w-full object-cover" />
          {isBusy && (
            <div className="absolute inset-0 flex items-center justify-center bg-white/70">
              <Loader2 className="h-5 w-5 animate-spin text-indigo-500" />
            </div>
          )}
          <button
            type="button"
            onClick={handleDelete}
            disabled={isBusy}
            aria-label="Remove photo"
            className="absolute right-1.5 top-1.5 flex h-6 w-6 items-center justify-center rounded-full bg-white/90 text-gray-600 shadow-sm hover:bg-white hover:text-red-500 disabled:opacity-60"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
        <div className="mt-3 text-center">
          <button
            type="button"
            onClick={() => inputRef.current?.click()}
            disabled={isBusy}
            className="text-sm font-medium text-indigo-600 hover:text-indigo-500 disabled:opacity-60"
          >
            Replace photo
          </button>
        </div>
        <input
          ref={inputRef}
          type="file"
          accept={ALLOWED_PHOTO_TYPES.join(",")}
          className="hidden"
          onChange={(e) => {
            if (e.target.files?.[0]) handleFile(e.target.files[0]);
            e.target.value = "";
          }}
        />
        {error && <p className="mt-2 text-center text-sm text-red-500">{error}</p>}
      </div>
    );
  }

  return (
    <div>
      <div
        onDragOver={(e) => { e.preventDefault(); setIsDragging(true); }}
        onDragLeave={() => setIsDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setIsDragging(false);
          if (e.dataTransfer.files?.[0]) handleFile(e.dataTransfer.files[0]);
        }}
        className={`flex flex-col items-center justify-center rounded-xl border-2 border-dashed px-6 py-8 text-center transition-colors ${
          isDragging ? "border-indigo-400 bg-indigo-50/60" : "border-gray-200 bg-gray-50/60"
        }`}
      >
        {isBusy ? (
          <Loader2 className="mb-2 h-5 w-5 animate-spin text-indigo-500" />
        ) : (
          <span className="mb-2 flex h-10 w-10 items-center justify-center rounded-full bg-white shadow-sm">
            <Camera className="h-5 w-5 text-gray-400" />
          </span>
        )}
        <p className="text-sm text-gray-500">
          {isBusy ? "Uploading…" : (
            <>
              Drag a photo here or{" "}
              <button
                type="button"
                onClick={() => inputRef.current?.click()}
                className="font-medium text-indigo-600 hover:text-indigo-500"
              >
                browse files
              </button>
            </>
          )}
        </p>
        <input
          ref={inputRef}
          type="file"
          accept={ALLOWED_PHOTO_TYPES.join(",")}
          className="hidden"
          onChange={(e) => {
            if (e.target.files?.[0]) handleFile(e.target.files[0]);
            e.target.value = "";
          }}
        />
      </div>
      {error && <p className="mt-2 text-sm text-red-500">{error}</p>}
    </div>
  );
}