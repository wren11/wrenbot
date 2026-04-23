using System;
using System.Collections.Generic;
using WrenBot.Types;
using WrenLib;

namespace WrenBot.PathFinding
{
    public struct PathFinderNode
    {
        public ushort X;
        public ushort Y;
    }

    public partial class PathFinder
    {
        public enum Directions
        {
            Four,
            Eight
        }
    }

    public static class MapPathFindingExtensions
    {
        public static List<PathFinderNode> FindPath(this Map map, Location from, Location to, bool useBackMatrix, NewProxy proxy, Form1 form)
        {
            if (map == null || from == null || to == null)
            {
                return null;
            }

            if (from.X == to.X && from.Y == to.Y)
            {
                return new List<PathFinderNode>();
            }

            byte[,] matrix = useBackMatrix ? map.CurrentBackMatrix : map.CurrentMatrix;
            if (matrix == null)
            {
                return null;
            }

            int width = matrix.GetLength(0);
            int height = matrix.GetLength(1);
            if (!IsInside(from.X, from.Y, width, height) || !IsInside(to.X, to.Y, width, height))
            {
                return null;
            }

            bool[,] visited = new bool[width, height];
            bool[,] hasParent = new bool[width, height];
            Point[,] parent = new Point[width, height];
            Queue<Point> queue = new Queue<Point>();

            Point start = new Point { X = from.X, Y = from.Y };
            Point target = new Point { X = to.X, Y = to.Y };

            visited[start.X, start.Y] = true;
            queue.Enqueue(start);

            int[] dx = new[] { 0, 1, 0, -1 };
            int[] dy = new[] { -1, 0, 1, 0 };

            while (queue.Count > 0)
            {
                Point current = queue.Dequeue();
                if (current.X == target.X && current.Y == target.Y)
                {
                    return BuildPath(parent, hasParent, start, target);
                }

                for (int i = 0; i < dx.Length; i++)
                {
                    int nx = current.X + dx[i];
                    int ny = current.Y + dy[i];

                    if (!IsInside(nx, ny, width, height) || visited[nx, ny] || !IsWalkable(matrix, nx, ny, target))
                    {
                        continue;
                    }

                    visited[nx, ny] = true;
                    parent[nx, ny] = current;
                    hasParent[nx, ny] = true;
                    queue.Enqueue(new Point { X = (ushort)nx, Y = (ushort)ny });
                }
            }

            return null;
        }

        private static bool IsInside(int x, int y, int width, int height)
        {
            return x >= 0 && y >= 0 && x < width && y < height;
        }

        private static bool IsWalkable(byte[,] matrix, int x, int y, Point target)
        {
            if (x == target.X && y == target.Y)
            {
                return true;
            }

            return matrix[x, y] != 0x00;
        }

        private static List<PathFinderNode> BuildPath(Point[,] parent, bool[,] hasParent, Point start, Point target)
        {
            List<PathFinderNode> reversed = new List<PathFinderNode>();
            Point current = target;

            while (!(current.X == start.X && current.Y == start.Y))
            {
                reversed.Add(new PathFinderNode { X = current.X, Y = current.Y });
                if (!hasParent[current.X, current.Y])
                {
                    return null;
                }
                current = parent[current.X, current.Y];
            }

            reversed.Reverse();
            return reversed;
        }
    }
}
