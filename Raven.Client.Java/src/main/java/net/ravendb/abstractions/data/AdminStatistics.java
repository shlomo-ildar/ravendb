package net.ravendb.abstractions.data;

import java.util.List;

//TODO: sync with remote
public class AdminStatistics {
  private String serverName;

  private int totalNumberOfRequests;
  private long uptime;
  private AdminMemoryStatistics memory;

  private List<LoadedDatabaseStatistics> loadedDatabases;
  //TODO:         public IEnumerable<FileSystemStats> LoadedFileSystems { get; set; }

  public String getServerName() {
    return serverName;
  }

  public void setServerName(String serverName) {
    this.serverName = serverName;
  }

  public int getTotalNumberOfRequests() {
    return totalNumberOfRequests;
  }

  public void setTotalNumberOfRequests(int totalNumberOfRequests) {
    this.totalNumberOfRequests = totalNumberOfRequests;
  }

  public long getUptime() {
    return uptime;
  }

  public void setUptime(long uptime) {
    this.uptime = uptime;
  }

  public AdminMemoryStatistics getMemory() {
    return memory;
  }

  public void setMemory(AdminMemoryStatistics memory) {
    this.memory = memory;
  }

  public List<LoadedDatabaseStatistics> getLoadedDatabases() {
    return loadedDatabases;
  }

  public void setLoadedDatabases(List<LoadedDatabaseStatistics> loadedDatabases) {
    this.loadedDatabases = loadedDatabases;
  }

}
