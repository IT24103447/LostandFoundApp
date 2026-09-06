import { AppHeader } from "../layout/AppHeader";
import { ReportLostItemWizard } from "../components/ReportLostItemWizard";

export function ReportLostItemPage() {
  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />
      <ReportLostItemWizard />
    </div>
  );
}
