export interface RunningProgram {
  processId: number;
  name: string;
  userName: string;
  sessionId: number;
  displayName: string;
  groupId: string;
  groupName: string;
  groupProcessName: string;
  windowTitles: string[];
  canEnd: boolean;
}

export interface ProgramGroup {
  id: string;
  name: string;
  processName: string;
  processes: RunningProgram[];
}
