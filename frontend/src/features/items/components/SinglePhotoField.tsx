import { useRef, useState, useEffect } from "react";
import { Camera, RefreshCw, Trash2 } from "lucide-react";
import { ALLOWED_PHOTO_TYPES } from "../schemas/reportFoundItemSchema";

type SinglePhotoFieldProps = {
  photos: File[];
  onChange: (photos: File[]) => void;
};

/**
 * A single-photo variant of PhotoDropzone. The found-item report only ever
 * attaches one photo (see the prototype and the story's "Optional Photo"
 * scenario, which is written in the singular), so this keeps the value model
 * simple (drag/drop or browse, then remove/replace) while still handing the
 * wizard a `File[]` so it slots into the same multipart submission shape the
 * backend already accepts.
 */
export function SinglePhotoField({ photos, onChange }: SinglePhotoFieldProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [preview, setPreview] = useState<string | null>(null);
  const file = photos[0] ?? null;

  useEffect(() => {
    if (!file) {
      setPreview(null);
      return;
    }
    const url = URL.createObjectURL(file);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [file]);

  const pick = (incoming: FileList | File[]) => {
    const accepted = Array.from(incoming).find((f) => ALLOWED_PHOTO_TYPES.includes(f.type));
    if (accepted) onChange([accepted]);
  };

  const hiddenInput = (
    <input
      ref={inputRef}
      type="file"
      accept={ALLOWED_PHOTO_TYPES.join(",")}
      className="hidden"
      onChange={(e) => {
        if (e.target.files?.length) pick(e.target.files);
        e.target.value = "";
      }}
    />
  );

  if (file) {
    return (
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-[1fr_auto] sm:items-stretch">
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-gray-100">
          {preview && (
            <img src={preview} alt={file.name} className="h-48 w-full object-cover sm:h-full" />
          )}
        </div>
        <div className="flex flex-row gap-3 sm:w-44 sm:flex-col">
          <button
            type="button"
            onClick={() => onChange([])}
            className="flex flex-1 items-center justify-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm font-semibold text-gray-700 shadow-sm transition-colors hover:bg-gray-50"
          >
            <Trash2 className="h-4 w-4" />
            Remove
          </button>
          <button
            type="button"
            onClick={() => inputRef.current?.click()}
            className="flex flex-1 items-center justify-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm font-semibold text-gray-700 shadow-sm transition-colors hover:bg-gray-50"
          >
            <RefreshCw className="h-4 w-4" />
            Replace photo
          </button>
        </div>
        {hiddenInput}
      </div>
    );
  }

  return (
    <div
      onDragOver={(e) => {
        e.preventDefault();
        setIsDragging(true);
      }}
      onDragLeave={() => setIsDragging(false)}
      onDrop={(e) => {
        e.preventDefault();
        setIsDragging(false);
        if (e.dataTransfer.files?.length) pick(e.dataTransfer.files);
      }}
      className={`flex flex-col items-center justify-center rounded-xl border-2 border-dashed px-6 py-10 text-center transition-colors ${
        isDragging ? "border-indigo-400 bg-indigo-50/60" : "border-gray-200 bg-gray-50/60"
      }`}
    >
      <span className="mb-3 flex h-11 w-11 items-center justify-center rounded-full bg-white shadow-sm">
        <Camera className="h-5 w-5 text-gray-400" />
      </span>
      <p className="text-[15px] font-medium text-gray-800">Drag and drop a photo here</p>
      <p className="mt-1 text-sm text-gray-500">
        or{" "}
        <button
          type="button"
          onClick={() => inputRef.current?.click()}
          className="font-medium text-indigo-600 hover:text-indigo-500"
        >
          Browse files
        </button>
      </p>
      <p className="mt-1 text-xs text-gray-400">JPG, PNG or WEBP</p>
      {hiddenInput}
    </div>
  );
}
