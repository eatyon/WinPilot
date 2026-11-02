export type Entry = {
  name: string;
  path: string;
  isDirectory: boolean;
  isReparsePoint: boolean;
  isSensitiveSystemPath: boolean;
  type: string;
  size: number | null;
  lastWriteTime: string;
};
export type DirectoryResult = {
  path: string;
  parentPath: string | null;
  entries: Entry[];
  isSensitiveSystemPath: boolean;
};
