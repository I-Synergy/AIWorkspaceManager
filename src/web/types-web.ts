/** Shapes the web layer needs that the shared contract does not express. */
import type { CatalogItem } from "../common/types";

export type GroupKey = "attention" | "pending" | "synced";

export interface InstalledGroup {
  key: GroupKey;
  label: string;
  items: CatalogItem[];
}
