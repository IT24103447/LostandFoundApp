/**
 * Found-item-only illustrations, so the Lost Item flow's Illustrations.tsx
 * never needs to change. FoundItemsIllustration and FoundMapIllustration are
 * new images the Lost Item flow has no equivalent of; SuccessCheckIllustration
 * (used on the found-item success page) is already generic/flow-agnostic and
 * is imported directly from the existing Illustrations.tsx unchanged.
 */
export function FoundItemsIllustration() {
  return (
    <div className="overflow-hidden rounded-xl border border-gray-200 bg-white">
      <img
        src="/illustrations/founditem.jpeg"
        alt="Found items illustration"
        className="h-40 w-full object-cover"
      />
    </div>
  );
}

export function FoundMapIllustration() {
  return (
    <div className="overflow-hidden rounded-xl border border-gray-200 bg-white">
      <img
        src="/illustrations/location2.png"
        alt="Map location illustration"
        className="h-40 w-full object-cover"
      />
    </div>
  );
}
