/* 
 * Copyright 2012-2016 Aerospike, Inc.
 *
 * Portions may be licensed to Aerospike, Inc. under one or more contributor
 * license agreements.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */
using System;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Aerospike.Client
{
	public class Cluster
	{
		private const int MaxSocketIdleSecondLimit = 60 * 60 * 24; // Limit maxSocketIdle to 24 hours
	
		// Initial host nodes specified by user.
		private volatile Host[] seeds;

		// All aliases for all nodes in cluster.
		protected internal readonly Dictionary<Host, Node> aliases;

		// Active nodes in cluster.
		private volatile Node[] nodes;

		// Hints for best node for a partition
		private volatile Dictionary<string, Node[][]> partitionMap;

		// IP translations.
		protected internal readonly Dictionary<string, string> ipMap;

		// User name in UTF-8 encoded bytes.
		protected internal readonly byte[] user;

		// Password in hashed format in bytes.
		protected internal byte[] password;

		// Random node index.
		private int nodeIndex;

		// Random partition replica index. 
		private int replicaIndex;

		// Size of node's synchronous connection pool.
		protected internal readonly int connectionQueueSize;

		// Initial connection timeout.
		protected internal readonly int connectionTimeout;

		// Maximum socket idle in milliseconds.
		protected internal readonly int maxSocketIdleMillis;

		// Interval in milliseconds between cluster tends.
		private readonly int tendInterval;

		// Tend thread variables.
		private Thread tendThread;
		private volatile bool tendValid;
        private volatile bool shouldTend = true;
        private ManualResetEvent tendAlerter;

		// Request prole replicas in addition to master replicas?
		private bool requestProleReplicas;

		// Should use "services-alternate" instead of "services" in info request?
		protected internal readonly bool useServicesAlternate;
		
		public Cluster(ClientPolicy policy, Host[] hosts)
		{
			this.seeds = hosts;

			if (policy.user != null && policy.user.Length > 0)
			{
				this.user = ByteUtil.StringToUtf8(policy.user);

				string pass = policy.password;

				if (pass == null)
				{
					pass = "";
				}

				if (! (pass.Length == 60 && pass.StartsWith("$2a$")))
				{
					pass = AdminCommand.HashPassword(pass);
				}
				this.password = ByteUtil.StringToUtf8(pass);
			}
			
			connectionQueueSize = policy.maxConnsPerNode;
			connectionTimeout = policy.timeout;
			maxSocketIdleMillis = 1000 * ((policy.maxSocketIdle <= MaxSocketIdleSecondLimit) ? policy.maxSocketIdle : MaxSocketIdleSecondLimit);
			tendInterval = policy.tendInterval;
			ipMap = policy.ipMap;
			requestProleReplicas = policy.requestProleReplicas;
			useServicesAlternate = policy.useServicesAlternate;

			aliases = new Dictionary<Host, Node>();
			nodes = new Node[0];
			partitionMap = new Dictionary<string, Node[][]>();
		}

		public virtual void InitTendThread(bool failIfNotConnected)
		{
            shouldTend = true;
			// Tend cluster until all nodes identified.
			WaitTillStabilized(failIfNotConnected);

			if (Log.DebugEnabled())
			{
				foreach (Host host in seeds)
				{
					Log.Debug("Add seed " + host);
				}
			}

			// Add other nodes as seeds, if they don't already exist.
			List<Host> seedsToAdd = new List<Host>(nodes.Length);
			foreach (Node node in nodes)
			{
				Host host = node.Host;
				if (!FindSeed(host))
				{
					seedsToAdd.Add(host);
				}

				// Disable double type support if some nodes don't support it.
				if (Value.UseDoubleType && !node.hasDouble)
				{
					if (Log.WarnEnabled())
					{
						Log.Warn("Some nodes don't support new double type.  Disabling.");
					}
					Value.UseDoubleType = false;
				}
	
				// Disable prole requests if some nodes don't support it.
				if (requestProleReplicas && !node.hasReplicasAll)
				{
					if (Log.WarnEnabled())
					{
						Log.Warn("Some nodes don't support 'replicas-all'.  Use 'replicas-master' for all nodes.");
					}
					requestProleReplicas = false;
				}
			}

			if (seedsToAdd.Count > 0)
			{
				AddSeeds(seedsToAdd.ToArray());
			}

			// Run cluster tend thread.
			tendValid = true;
			tendAlerter = new ManualResetEvent(false);
			tendThread = new Thread(new ThreadStart(this.Run));
			tendThread.Name = "tend";
			tendThread.IsBackground = true;
			tendThread.Start();
		}

		public void AddSeeds(Host[] hosts)
		{
			// Use copy on write semantics.
			Host[] seedArray = new Host[seeds.Length + hosts.Length];
			int count = 0;

			// Add existing seeds.
			foreach (Host seed in seeds)
			{
				seedArray[count++] = seed;
			}

			// Add new seeds
			foreach (Host host in hosts)
			{
				if (Log.DebugEnabled())
				{
					Log.Debug("Add seed " + host);
				}
				seedArray[count++] = host;
			}

			// Replace nodes with copy.
			seeds = seedArray;
		}

		private bool FindSeed(Host search)
		{
			foreach (Host seed in seeds)
			{
				if (seed.Equals(search))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Tend the cluster until it has stabilized and return control.
		/// This helps avoid initial database request timeout issues when
		/// a large number of threads are initiated at client startup.
		/// 
		/// If the cluster has not stabilized by the timeout, return
		/// control as well.  Do not return an error since future 
		/// database requests may still succeed.
		/// </summary>
		private void WaitTillStabilized(bool failIfNotConnected)
		{
			DateTime limit = DateTime.UtcNow.AddMilliseconds(connectionTimeout);
			int count = -1;

			do
			{
				Tend(failIfNotConnected);

				// Check to see if cluster has changed since the last Tend().
				// If not, assume cluster has stabilized and return.
				if (count == nodes.Length)
				{
					return;
				}

				Util.Sleep(1);
				count = nodes.Length;
			} while (DateTime.UtcNow < limit);
		}

		public void Run()
		{
			while (tendValid)
			{
				// Tend cluster.
				try
				{
					Tend(false);
				}
				catch (Exception e)
				{
					if (Log.WarnEnabled())
					{
						Log.Warn("Cluster tend failed: " + Util.GetErrorMessage(e));
					}
				}
				tendAlerter.WaitOne(tendInterval);
			}
		}

		/// <summary>
		/// Check health of all nodes in the cluster.
		/// </summary>
		private void Tend(bool failIfNotConnected)
		{
			// All node additions/deletions are performed in tend thread.		
			// If active nodes don't exist, seed cluster.
			if (nodes.Length == 0)
			{
				SeedNodes(failIfNotConnected);
			}

			// Clear node reference counts.
			foreach (Node node in nodes)
			{
				node.referenceCount = 0;
			}

			// Refresh all known nodes.
			List<Host> friendList = new List<Host>();
			int refreshCount = 0;

			foreach (Node node in nodes)
			{
				try
				{
					if (node.Active && shouldTend)
					{
						node.Refresh(friendList);
						node.failures = 0;
						refreshCount++;
					}
				}
				catch (Exception e)
				{
					node.failures++;

					if (tendValid && Log.InfoEnabled())
					{
						Log.Info("Node " + node + " refresh failed: " + Util.GetErrorMessage(e));
					}
				}
			}

			// Handle nodes changes determined from refreshes.
			List<Node> addList = FindNodesToAdd(friendList);
			List<Node> removeList = FindNodesToRemove(refreshCount);

			// Remove nodes in a batch.
			if (removeList.Count > 0)
			{
				RemoveNodes(removeList);
			}

			// Add nodes in a batch.
			if (addList.Count > 0)
			{
				AddNodes(addList);
			}
		}

		protected internal int UpdatePartitions(Connection conn, Node node)
		{
			PartitionParser parser = new PartitionParser(conn, node, partitionMap, Node.PARTITIONS, requestProleReplicas);

			if (parser.IsPartitionMapCopied)
			{
				partitionMap = parser.PartitionMap;
			}
			return parser.Generation;
		}

		private bool SeedNodes(bool failIfNotConnected)
		{
			// Must copy array reference for copy on write semantics to work.
			Host[] seedArray = seeds;
			Exception[] exceptions = null;

			// Add all nodes at once to avoid copying entire array multiple times.
			List<Node> list = new List<Node>();

			for (int i = 0; i < seedArray.Length && shouldTend; i++)
			{
				Host seed = seedArray[i];

				// Check if seed already exists in cluster.
				if (aliases.ContainsKey(seed))
				{
					continue;
				}

				try
				{
					NodeValidator nv = new NodeValidator();
					nv.SeedNodes(this, seed, list);
				}
				catch (Exception e)
				{
					if (Log.WarnEnabled())
					{
						Log.Warn("Seed " + seed + " failed: " + Util.GetErrorMessage(e));
					}

					// Store exception and try next host
					if (failIfNotConnected)
					{
						if (exceptions == null)
						{
							exceptions = new Exception[seedArray.Length];
						}
						exceptions[i] = e;
					}
				}
			}

			if (list.Count > 0)
			{
				AddNodesCopy(list);
				return true;
			}
			else if (failIfNotConnected)
			{
				StringBuilder sb = new StringBuilder(500);
				sb.AppendLine("Failed to connect to host(s): ");

				for (int i = 0; i < seedArray.Length; i++)
				{
					sb.Append(seedArray[i]);
					sb.Append(' ');

					Exception ex = exceptions[i];

					if (ex != null)
					{
						sb.AppendLine(ex.Message);
					}
				}
				throw new AerospikeException.Connection(sb.ToString());
			}
			return false;
		}

		private List<Node> FindNodesToAdd(List<Host> hosts)
		{
			List<Node> list = new List<Node>(hosts.Count);

			foreach (Host host in hosts)
			{
				try
				{
					NodeValidator nv = new NodeValidator();
					nv.ValidateNode(this, host);

					Node node = FindNode(nv.name, list);

					if (node != null)
					{
						// Duplicate node name found.  This usually occurs when the server 
						// services list contains both internal and external IP addresses 
						// for the same node.  Add new host to list of alias filters
						// and do not add new node.
						nv.conn.Close();
						node.referenceCount++;
						node.AddAlias(host);
						aliases[host] = node;
						continue;
					}
					node = CreateNode(nv);
					list.Add(node);
				}
				catch (Exception e)
				{
					if (Log.WarnEnabled())
					{
						Log.Warn("Add node " + host + " failed: " + Util.GetErrorMessage(e));
					}
				}
			}
			return list;
		}

		private Node FindNode(string nodeName, List<Node> localList)
		{
			foreach (Node node in localList)
			{
				if (node.Name.Equals(nodeName))
				{
					return node;
				}
			}

			// Must copy array reference for copy on write semantics to work.
			Node[] nodeArray = nodes;

			foreach (Node node in nodeArray)
			{
				if (node.Name.Equals(nodeName))
				{
					return node;
				}
			}
			return null;
		}

		protected internal virtual Node CreateNode(NodeValidator nv)
		{
			return new Node(this, nv);
		}

		private List<Node> FindNodesToRemove(int refreshCount)
		{
			List<Node> removeList = new List<Node>();

			foreach (Node node in nodes)
			{
				if (!node.Active)
				{
					// Inactive nodes must be removed.
					removeList.Add(node);
					continue;
				}

				switch (nodes.Length)
				{
				case 1:
					// Single node clusters rely on whether it responded to info requests.
					if (node.failures >= 5)
					{
						// 5 consecutive info requests failed.
						// Remove node.  Seeds will be tried in next cluster tend iteration.
						removeList.Add(node);
					}
					break;

				case 2:
					// Two node clusters require at least one successful refresh before removing.
					if (refreshCount == 1 && node.referenceCount == 0 && node.failures > 0)
					{
						// Node is not referenced nor did it respond.
						removeList.Add(node);
					}
					break;

				default:
					// Multi-node clusters require two successful node refreshes before removing.
					if (refreshCount >= 2 && node.referenceCount == 0)
					{
						// Node is not referenced by other nodes.
						// Check if node responded to info request.
						if (node.failures == 0)
						{
							// Node is alive, but not referenced by other nodes.  Check if mapped.
							if (!FindNodeInPartitionMap(node))
							{
								// Node doesn't have any partitions mapped to it.
								// There is no point in keeping it in the cluster.
								removeList.Add(node);
							}
						}
						else
						{
							// Node not responding. Remove it.
							removeList.Add(node);
						}
					}
					break;
				}
			}
			return removeList;
		}

		private bool FindNodeInPartitionMap(Node filter)
		{
			foreach (Node[][] replicaArray in partitionMap.Values)
			{
				foreach (Node[] nodeArray in replicaArray)
				{
					foreach (Node node in nodeArray)
					{
						// Use reference equality for performance.
						if (node == filter)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		private void AddNodes(List<Node> nodesToAdd)
		{
			// Add all nodes at once to avoid copying entire array multiple times.		
			foreach (Node node in nodesToAdd)
			{
				AddAliases(node);
			}
			AddNodesCopy(nodesToAdd);
		}

		protected internal void AddAliases(Node node)
		{
			// Add node's aliases to global alias set.
			// Aliases are only used in tend thread, so synchronization is not necessary.
			foreach (Host alias in node.Aliases)
			{
				aliases[alias] = node;
			}
		}

		/// <summary>
		/// Add nodes using copy on write semantics.
		/// </summary>
		private void AddNodesCopy(List<Node> nodesToAdd)
		{
			// Create temporary nodes array.
			Node[] nodeArray = new Node[nodes.Length + nodesToAdd.Count];
			int count = 0;

			// Add existing nodes.
			foreach (Node node in nodes)
			{
				nodeArray[count++] = node;
			}

			// Add new nodes
			foreach (Node node in nodesToAdd)
			{
				if (Log.InfoEnabled())
				{
					Log.Info("Add node " + node);
				}
				nodeArray[count++] = node;
			}

			// Replace nodes with copy.
			nodes = nodeArray;
		}

		private void RemoveNodes(List<Node> nodesToRemove)
		{
			// There is no need to delete nodes from partitionWriteMap because the nodes 
			// have already been set to inactive. Further connection requests will result 
			// in an exception and a different node will be tried.

			// Cleanup node resources.
			foreach (Node node in nodesToRemove)
			{
				// Remove node's aliases from cluster alias set.
				// Aliases are only used in tend thread, so synchronization is not necessary.
				foreach (Host alias in node.Aliases)
				{
					// Log.debug("Remove alias " + alias);
					aliases.Remove(alias);
				}

				if (tendValid)
				{
					node.Close();
				}
				else
				{
					return;
				}
			}

			// Remove all nodes at once to avoid copying entire array multiple times.
			RemoveNodesCopy(nodesToRemove);
		}
	
		/// <summary>
		/// Remove nodes using copy on write semantics.
		/// </summary>
		private void RemoveNodesCopy(List<Node> nodesToRemove)
		{
			// Create temporary nodes array.
			// Since nodes are only marked for deletion using node references in the nodes array,
			// and the tend thread is the only thread modifying nodes, we are guaranteed that nodes
			// in nodesToRemove exist.  Therefore, we know the final array size. 
			Node[] nodeArray = new Node[nodes.Length - nodesToRemove.Count];
			int count = 0;

			// Add nodes that are not in remove list.
			foreach (Node node in nodes)
			{
				if (FindNode(node, nodesToRemove))
				{
					if (tendValid && Log.InfoEnabled())
					{
						Log.Info("Remove node " + node);
					}
				}
				else
				{
					nodeArray[count++] = node;
				}
			}

			// Do sanity check to make sure assumptions are correct.
			if (count < nodeArray.Length)
			{
				if (Log.WarnEnabled())
				{
					Log.Warn("Node remove mismatch. Expected " + nodeArray.Length + " Received " + count);
				}
				// Resize array.
				Node[] nodeArray2 = new Node[count];
				Array.Copy(nodeArray, 0, nodeArray2, 0, count);
				nodeArray = nodeArray2;
			}

			// Replace nodes with copy.
			nodes = nodeArray;
		}

		private static bool FindNode(Node search, List<Node> nodeList)
		{
			foreach (Node node in nodeList)
			{
				if (node.Equals(search))
				{
					return true;
				}
			}
			return false;
		}

		public bool Connected
		{
			get
			{
				// Must copy array reference for copy on write semantics to work.
				Node[] nodeArray = nodes;

				if (nodeArray.Length > 0 && tendValid)
				{
					// Even though nodes exist, they may not be currently responding.  Check further.
					foreach (Node node in nodeArray)
					{
						// Mark connected if any node is active and cluster tend consecutive info request 
						// failures are less than 5.
						if (node.active && node.failures < 5)
						{
							return true;
						}
					}
				}
				return false; 
			}
		}

		public Node GetReadNode(Partition partition, Replica replica)
		{
			switch (replica)
			{
				case Replica.MASTER:
					return GetMasterNode(partition);

				case Replica.MASTER_PROLES:
					return GetMasterProlesNode(partition);

				default:
				case Replica.RANDOM:
					return GetRandomNode();
			}
		}
		
		public Node GetMasterNode(Partition partition)
		{
			// Must copy hashmap reference for copy on write semantics to work.
			Dictionary<string, Node[][]> map = partitionMap;
			Node[][] replicaArray;

			if (map.TryGetValue(partition.ns, out replicaArray))
			{
				Node node = replicaArray[0][partition.partitionId];

				if (node != null && node.Active)
				{
					return node;
				}
			}
			/*
			if (Log.debugEnabled()) {
				Log.debug("Choose random node for " + partition);
			}
			*/
			return GetRandomNode();
		}

		public Node GetMasterProlesNode(Partition partition)
		{
			// Must copy hashmap reference for copy on write semantics to work.
			Dictionary<string, Node[][]> map = partitionMap;
			Node[][] replicaArray;

			if (map.TryGetValue(partition.ns, out replicaArray))
			{
				for (int i = 0; i < replicaArray.Length; i++)
				{
					int index = Math.Abs(replicaIndex % replicaArray.Length);
					Interlocked.Increment(ref replicaIndex);
					Node node = replicaArray[index][partition.partitionId];

					if (node != null && node.Active)
					{
						return node;
					}
				}
			}
			/*
			if (Log.debugEnabled()) {
				Log.debug("Choose random node for " + partition);
			}
			*/
			return GetRandomNode();
		}
		
		public Node GetRandomNode()
		{
			// Must copy array reference for copy on write semantics to work.
			Node[] nodeArray = nodes;
    
			for (int i = 0; i < nodeArray.Length; i++)
			{
				// Must handle concurrency with other non-tending threads, so nodeIndex is consistent.
				int index = Math.Abs(nodeIndex % nodeArray.Length);
				Interlocked.Increment(ref nodeIndex);
				Node node = nodeArray[index];

				if (node.Active)
				{
					return node;
				}
			}
			throw new AerospikeException.InvalidNode();
		}

		public Node[] Nodes
		{
			get
			{
				// Must copy array reference for copy on write semantics to work.
				Node[] nodeArray = nodes;
				return nodeArray;
			}
		}

		public Node GetNode(string nodeName)
		{
			Node node = FindNode(nodeName);

			if (node == null)
			{
				throw new AerospikeException.InvalidNode();
			}
			return node;
		}

		private Node FindNode(string nodeName)
		{
			// Must copy array reference for copy on write semantics to work.
			Node[] nodeArray = nodes;

			foreach (Node node in nodeArray)
			{
				if (node.Name.Equals(nodeName))
				{
					return node;
				}
			}
			return null;
		}

		public void PrintPartitionMap()
		{
			foreach (KeyValuePair<String,Node[][]> entry in partitionMap)
			{
				String ns = entry.Key;
				Node[][] replicaArray = entry.Value;

				for (int i = 0; i < replicaArray.Length; i++)
				{
					Node[] nodeArray = replicaArray[i];
					int max = nodeArray.Length;

					for (int j = 0; j < max; j++)
					{
						Node node = nodeArray[j];

						if (node != null)
						{
							Log.Info(ns + ',' + i + ',' + j + ',' + node);
						}
					}
				}
			}
		}
		
		protected internal void ChangePassword(byte[] user, string password)
		{
			if (this.user != null && Util.ByteArrayEquals(user, this.user))
			{
				this.password = ByteUtil.StringToUtf8(password);
			}
		}

		public void Close()
		{
			tendValid = false;
            		shouldTend = false;
			if (tendAlerter != null)
			{
				tendAlerter.Set();
			}

			// Must copy array reference for copy on write semantics to work.
			Node[] nodeArray = nodes;

			foreach (Node node in nodeArray)
			{
				node.Close();
			}
		}
	}
}
