import { useRef, useState, useEffect } from "react";
import { Camera, X, Plus } from "lucide-react";
import { ALLOWED_PHOTO_TYPES, MAX_PHOTOS } from "../schemas/reportLostItemSchema";

type PhotoDropzoneProps = {
  photos: File[];
  onChange: (photos: File[]) => void;
  maxPhotos?: number; // defaults to Lost Item's MAX_PHOTOS (5)
};

export function PhotoDropzone({ photos, onChange, maxPhotos = MAX_PHOTOS }: PhotoDropzoneProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [previews, setPreviews] = useState<string[]>([]);

  useEffect(() => {
    const urls = photos.map((f) => URL.createObjectURL(f));
    setPreviews(urls);
    return () => urls.forEach((u) => URL.revokeObjectURL(u));
  }, [photos]);

  const addFiles = (incoming: FileList | File[]) => {
    const accepted = Array.from(incoming).filter((f) => ALLOWED_PHOTO_TYPES.includes(f.type));
    const room = Math.max(0, maxPhotos - photos.length);
    onChange([...photos, ...accepted.slice(0, room)]);
  };

  const removeAt = (index: number) => {
    onChange(photos.filter((_, i) => i !== index));
  };

  const canAddMore = photos.length < maxPhotos;

  return (
    <div>
      {canAddMore && (
        <div
          onDragOver={(e) => {
            e.preventDefault();
            setIsDragging(true);
          }}
          onDragLeave={() => setIsDragging(false)}
          onDrop={(e) => {
            e.preventDefault();
            setIsDragging(false);
            if (e.dataTransfer.files?.length) addFiles(e.dataTransfer.files);
          }}
          className={`flex flex-col items-center justify-center rounded-xl border-2 border-dashed px-6 py-10 text-center transition-colors ${
            isDragging ? "border-indigo-400 bg-indigo-50/60" : "border-gray-200 bg-gray-50/60"
          }`}
        >
          <span className="mb-3 flex h-11 w-11 items-center justify-center rounded-full bg-white shadow-sm">
            <Camera className="h-5 w-5 text-gray-400" />
          </span>
          <p className="text-[15px] font-medium text-gray-800">Drag and drop photos here</p>
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
          <input
            ref={inputRef}
            type="file"
            accept={ALLOWED_PHOTO_TYPES.join(",")}
            multiple
            className="hidden"
            onChange={(e) => {
              if (e.target.files?.length) addFiles(e.target.files);
              e.target.value = "";
            }}
          />
        </div>
      )}

      {photos.length > 0 && (
        <div className={`grid grid-cols-3 gap-3 ${canAddMore ? "mt-4" : ""}`}>
          {photos.map((file, i) => (
            <div key={`${file.name}-${i}`} className="group relative aspect-square overflow-hidden rounded-xl border border-gray-200 bg-gray-100">
              {previews[i] && (
                <img src={previews[i]} alt={file.name} className="h-full w-full object-cover" />
              )}
              <button
                type="button"
                onClick={() => removeAt(i)}
                aria-label={`Remove ${file.name}`}
                className="absolute right-1.5 top-1.5 flex h-6 w-6 items-center justify-center rounded-full bg-white/90 text-gray-600 shadow-sm transition-colors hover:bg-white hover:text-red-500"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          ))}
        </div>
      )}

      {photos.length > 0 && canAddMore && (
        <button
          type="button"
          onClick={() => inputRef.current?.click()}
          className="mt-3 flex items-center gap-1.5 text-sm font-medium text-indigo-600 hover:text-indigo-500"
        >
          <Plus className="h-4 w-4" />
          Add another photo
        </button>
      )}
    </div>
  );
}
