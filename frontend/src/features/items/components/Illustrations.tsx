export function ItemsIllustration() {
  return (
    <div className="overflow-hidden rounded-xl border border-gray-200 bg-white">
      <img
        src="/illustrations/Items.png"
        alt="Items illustration"
        className="h-40 w-full object-cover"
      />
    </div>
  );
}

export function MapIllustration({ caption }: { caption: string }) {
  return (
    <div className="overflow-hidden rounded-xl border border-gray-200 bg-white">
      <img
        src="/illustrations/location.png"
        alt="Location illustration"
        className="h-40 w-full object-cover"
      />

      <p className="border-t border-gray-100 px-4 py-3 text-sm font-medium text-gray-700">
        {caption}
      </p>
    </div>
  );
}

export function SuccessCheckIllustration() {
  return (
    <svg viewBox="0 0 160 120" className="mx-auto h-24 w-auto">
      <circle cx="30" cy="20" r="3" fill="#6EE7B7" />
      <circle cx="135" cy="18" r="2.5" fill="#818CF8" />
      <circle cx="145" cy="55" r="2" fill="#6EE7B7" />
      <circle cx="18" cy="60" r="2" fill="#A5B4FC" />
      <path d="M120 30 l6 6 m-6 0 l6 -6" stroke="#6EE7B7" strokeWidth="2.5" strokeLinecap="round" />
      <path d="M25 75 l5 5 m-5 0 l5 -5" stroke="#818CF8" strokeWidth="2.5" strokeLinecap="round" />
      <circle cx="80" cy="55" r="40" fill="url(#successGradient)" />
      <path d="M63 56 l12 12 24 -26" stroke="white" strokeWidth="7" strokeLinecap="round" strokeLinejoin="round" fill="none" />
      <defs>
        <linearGradient id="successGradient" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor="#4338CA" />
          <stop offset="100%" stopColor="#7C3AED" />
        </linearGradient>
      </defs>
    </svg>
  );
}
