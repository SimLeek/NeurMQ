import zmq
import time

def main():
    # Initialize ZeroMQ context
    context = zmq.Context()

    # Initialize subscriber socket
    subscriber = context.socket(zmq.SUB)
    subscriber.connect("tcp://localhost:5555")
    # Subscribe to relevant topics
    topics = ["SET_JOINT_FORCES", "GET_DOF_START_INDICES", "GET_JOINT_POSITIONS",
              "GET_JOINT_VELOCITIES", "GET_JOINT_FORCES", "GET_JOINT_EXTERNAL_FORCES", "ERROR"]
    for topic in topics:
        subscriber.subscribe(topic)
    print("Subscriber connected to tcp://localhost:5555")

    # Initialize publisher socket
    publisher = context.socket(zmq.PUB)
    publisher.connect("tcp://localhost:5556")
    print("Publisher connected to tcp://localhost:5556")

    # Allow time for connections to establish
    time.sleep(1)

    try:
        while True:
            # Check for incoming messages non-blocking
            try:
                topic = subscriber.recv_string(zmq.NOBLOCK)
                message = subscriber.recv_string()
                print(f"Received: {topic}:{message}")
            except zmq.Again:
                pass  # No message available

            print("\nAvailable commands:")
            print("1. Set joint forces (e.g., 100,200,300)")
            print("2. Get DOF start indices")
            print("3. Get joint positions")
            print("4. Get joint velocities")
            print("5. Get joint forces")
            print("6. Get joint external forces")
            print("7. Exit")
            choice = input("Enter choice (1-7): ")

            command = None
            if choice == "1":
                forces = input("Enter forces (comma-separated, e.g., 100,200,300): ")
                command = f"SET_JOINT_FORCES:{forces}"
            elif choice == "2":
                command = "GET_DOF_START_INDICES:"
            elif choice == "3":
                command = "GET_JOINT_POSITIONS:"
            elif choice == "4":
                command = "GET_JOINT_VELOCITIES:"
            elif choice == "5":
                command = "GET_JOINT_FORCES:"
            elif choice == "6":
                command = "GET_JOINT_EXTERNAL_FORCES:"
            elif choice == "7":
                print("Exiting...")
                break
            else:
                print("Invalid choice, please try again.")
                continue

            if command:
                # Send command
                publisher.send_string(command)
                print(f"Sent: {command}")

    finally:
        # Clean up
        subscriber.close()
        publisher.close()
        context.term()

if __name__ == "__main__":
    main()